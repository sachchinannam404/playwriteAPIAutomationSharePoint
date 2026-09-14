using System.Collections.Concurrent;
using System.Diagnostics;
using ApiAutomation.Configuration;
using ApiAutomation.Core.Interfaces;
using ApiAutomation.Core.Models;
using ApiAutomation.Core.Validation;

namespace ApiAutomation.Application;

/// <summary>Coordinates definition validation, API execution, validation, result persistence, and UI progress notifications.</summary>
public sealed class ExecutionEngine(ITestCaseProvider tests, IResultRepository results, IRequestBuilder requests,
    IApiClient api, IValidationEngine validation, FrameworkSettings settings, IExecutionProgressSink progress)
{
    private readonly object _progressLock = new();

    /// <summary>Runs the selected definitions sequentially or with bounded parallelism and returns a stable ordered summary.</summary>
    public async Task<ExecutionSummary> ExecuteAsync(ExecutionFilter filter, CancellationToken cancellationToken)
    {
        if (!settings.Environments.TryGetValue(filter.Environment, out var environment)) throw new InvalidOperationException($"Unknown environment '{filter.Environment}'.");
        var id = Guid.NewGuid().ToString("N"); var started = DateTimeOffset.UtcNow;
        await results.CreateRunAsync(id, filter, cancellationToken).ConfigureAwait(false);
        var definitions = await tests.GetAsync(filter, cancellationToken).ConfigureAwait(false);
        var output = new ConcurrentBag<TestExecutionResult>();
        if (filter.FailFast || filter.Parallelism <= 1)
        {
            foreach (var definition in definitions)
            {
                var result = await ExecuteOneAsync(id, definition, environment, cancellationToken).ConfigureAwait(false);
                output.Add(result); ReportProgress(result);
                if (filter.FailFast && result.Status is ExecutionStatus.Failed or ExecutionStatus.Error or ExecutionStatus.Invalid) break;
            }
        }
        else
        {
            using var gate = new SemaphoreSlim(filter.Parallelism);
            var tasks = definitions.Select(async definition =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var result = await ExecuteOneAsync(id, definition, environment, cancellationToken).ConfigureAwait(false);
                    output.Add(result); ReportProgress(result);
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        var summary = new ExecutionSummary(id, started, DateTimeOffset.UtcNow, output.OrderBy(x => x.TestCaseId, StringComparer.Ordinal).ToArray());
        try { await results.CompleteRunAsync(summary, cancellationToken).ConfigureAwait(false); } catch (Exception e) { Console.Error.WriteLine($"Run publication failed: {e.Message}"); }
        progress.Complete(summary); return summary;
    }
    /// <summary>Serializes progress notifications because a GUI sink may not be thread-safe.</summary>
    private void ReportProgress(TestExecutionResult result)
    {
        lock (_progressLock) progress.Report(result);
    }
    /// <summary>Executes and validates one case, creating an auditable result for every outcome category.</summary>
    private async Task<TestExecutionResult> ExecuteOneAsync(string executionId, TestCaseDefinition test, EnvironmentConfiguration env, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow; var watch = Stopwatch.StartNew();
        TestExecutionResult Create(ExecutionStatus status, int attempt, ApiResponse? response = null, IReadOnlyList<ValidationResult>? validations = null, string? reason = null) => new(executionId, test.Id, attempt, env.Name, test.ApiName, test.Scenario, test.Method, test.Endpoint, status, response?.StatusCode, test.ExpectedStatusCode, validations ?? [], reason, Mask(test.Payload), response is null ? null : Mask(response.Body is { Length: > 0 } body && body.Length > settings.ResponseBodyLimit ? body[..settings.ResponseBodyLimit] + " [truncated]" : response.Body), watch.Elapsed, attempt - 1, started, DateTimeOffset.UtcNow);
        try
        {
            var dataErrors = TestDefinitionValidator.Validate(test);
            if (dataErrors.Count > 0) return await PublishAsync(Create(ExecutionStatus.Invalid, 1, reason: string.Join(" ", dataErrors)), ct).ConfigureAwait(false);
            for (var attempt = 1; attempt <= settings.MaxRetries + 1; attempt++)
            {
                try
                {
                    var response = await api.SendAsync(requests.Build(test, env, $"{executionId}:{test.Id}:{attempt}"), ct).ConfigureAwait(false);
                    var checks = validation.Validate(test, response);
                    var status = checks.All(x => x.Passed) ? ExecutionStatus.Passed : ExecutionStatus.Failed;
                    return await PublishAsync(Create(status, attempt, response, checks, checks.FirstOrDefault(x => !x.Passed)?.FailureMessage), ct).ConfigureAwait(false);
                }
                catch (Exception e) when (attempt <= settings.MaxRetries && IsTransient(e)) { await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false); }
                catch (Exception e) { return await PublishAsync(Create(ExecutionStatus.Error, attempt, reason: e.Message), ct).ConfigureAwait(false); }
            }
            throw new InvalidOperationException("Unreachable retry state.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            var fallback = Create(ExecutionStatus.Error, 1, reason: e.Message);
            return await PublishAsync(fallback, ct).ConfigureAwait(false);
        }
    }
    /// <summary>Attempts result publication separately so a publication outage cannot rewrite the functional result.</summary>
    private async Task<TestExecutionResult> PublishAsync(TestExecutionResult result, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++) try { await results.PersistAsync(result, ct).ConfigureAwait(false); return result with { PublicationSucceeded = true }; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (attempt < settings.MaxRetries && IsTransient(e)) { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct).ConfigureAwait(false); }
        catch (Exception e) { return result with { PublicationFailure = e.Message }; }
    }
    /// <summary>Identifies technical failures that are safe to retry without repeating validation failures.</summary>
    private static bool IsTransient(Exception e) => e is HttpRequestException or TimeoutException or TaskCanceledException;
    /// <summary>Masks common secret key/value patterns before log or SharePoint persistence.</summary>
    private static string? Mask(string? value) => value is null ? null : System.Text.RegularExpressions.Regex.Replace(value, "(?i)(authorization|token|password|secret)\\s*[:=]\\s*[\\\"']?[^,\\s\\\"']+", "$1: ***");
}
