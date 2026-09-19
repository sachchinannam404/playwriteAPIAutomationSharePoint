using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ApiAutomation.Configuration;
using ApiAutomation.Core.Interfaces;
using ApiAutomation.Core.Models;
using ApiAutomation.Core.Validation;

namespace ApiAutomation.Application;

/// <summary>Coordinates definition validation, API execution, validation, result persistence, and UI progress notifications.</summary>
public sealed class ExecutionEngine(
    ITestCaseProvider tests,
    IResultRepository results,
    IRequestBuilder requests,
    IApiClient api,
    IValidationEngine validation,
    FrameworkSettings settings,
    IExecutionProgressSink progress)
{
    private readonly object _progressLock = new();
    private static readonly Regex SensitivePattern = new(
        @"(?i)(authorization|token|password|secret|api[_-]?key|client[_-]?secret|access[_-]?token)\s*[:=]\s*\S+",
        RegexOptions.Compiled);

    private static readonly HashSet<string> SensitiveJsonKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "token", "secret", "authorization", "apikey", "api_key", "client_secret", "access_token", "refresh_token"
    };

    /// <summary>Runs the selected definitions sequentially or with bounded parallelism and returns a stable ordered summary.</summary>
    public async Task<ExecutionSummary> ExecuteAsync(ExecutionFilter filter, CancellationToken cancellationToken)
    {
        if (!settings.Environments.TryGetValue(filter.Environment, out var environment))
            throw new InvalidOperationException($"Unknown environment '{filter.Environment}'.");

        var id = Guid.NewGuid().ToString("N");
        var started = DateTimeOffset.UtcNow;
        await results.CreateRunAsync(id, filter, cancellationToken).ConfigureAwait(false);

        var definitions = await tests.GetAsync(filter, cancellationToken).ConfigureAwait(false);
        var output = new ConcurrentBag<TestExecutionResult>();

        if (filter.FailFast || filter.Parallelism <= 1)
        {
            foreach (var definition in definitions)
            {
                var result = await ExecuteOneAsync(id, definition, environment, cancellationToken).ConfigureAwait(false);
                output.Add(result);
                ReportProgress(result);
                if (filter.FailFast && result.Status is ExecutionStatus.Failed or ExecutionStatus.Error or ExecutionStatus.Invalid)
                    break;
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
                    output.Add(result);
                    ReportProgress(result);
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        var summary = new ExecutionSummary(id, started, DateTimeOffset.UtcNow, output.OrderBy(x => x.TestCaseId, StringComparer.Ordinal).ToArray());
        try
        {
            await results.CompleteRunAsync(summary, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Run publication failed: {e.Message}");
        }

        progress.Complete(summary);
        return summary;
    }

    private void ReportProgress(TestExecutionResult result)
    {
        lock (_progressLock) progress.Report(result);
    }

    private async Task<TestExecutionResult> ExecuteOneAsync(
        string executionId,
        TestCaseDefinition test,
        EnvironmentConfiguration env,
        CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();

        TestExecutionResult Create(
            ExecutionStatus status,
            int attempt,
            ApiResponse? response = null,
            IReadOnlyList<ValidationResult>? validations = null,
            string? reason = null) =>
            new(
                executionId,
                test.Id,
                attempt,
                env.Name,
                test.ApiName,
                test.Scenario,
                test.Method,
                test.Endpoint,
                status,
                response?.StatusCode,
                test.ExpectedStatusCode,
                validations ?? [],
                reason,
                Mask(test.Payload),
                response is null ? null : Mask(Truncate(response.Body)),
                watch.Elapsed,
                attempt - 1,
                started,
                DateTimeOffset.UtcNow);

        try
        {
            var dataErrors = TestDefinitionValidator.Validate(test);
            if (dataErrors.Count > 0)
                return await PublishAsync(Create(ExecutionStatus.Invalid, 1, reason: string.Join(" ", dataErrors)), ct).ConfigureAwait(false);

            for (var attempt = 1; attempt <= settings.MaxRetries + 1; attempt++)
            {
                try
                {
                    var response = await api
                        .SendAsync(requests.Build(test, env, $"{executionId}:{test.Id}:{attempt}"), ct)
                        .ConfigureAwait(false);

                    // Treat configured status codes as transient so callers can retry 429/5xx without coding it per test.
                    if (attempt <= settings.MaxRetries && settings.RetryableStatusCodes.Contains(response.StatusCode))
                    {
                        await DelayWithBackoffAsync(attempt, ct).ConfigureAwait(false);
                        continue;
                    }

                    var checks = validation.Validate(test, response);
                    var status = checks.All(x => x.Passed) ? ExecutionStatus.Passed : ExecutionStatus.Failed;
                    return await PublishAsync(
                        Create(status, attempt, response, checks, checks.FirstOrDefault(x => !x.Passed)?.FailureMessage),
                        ct).ConfigureAwait(false);
                }
                catch (Exception e) when (attempt <= settings.MaxRetries && IsTransient(e))
                {
                    await DelayWithBackoffAsync(attempt, ct).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    return await PublishAsync(Create(ExecutionStatus.Error, attempt, reason: e.Message), ct).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException("Unreachable retry state.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return await PublishAsync(Create(ExecutionStatus.Error, 1, reason: e.Message), ct).ConfigureAwait(false);
        }
    }

    private async Task<TestExecutionResult> PublishAsync(TestExecutionResult result, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await results.PersistAsync(result, ct).ConfigureAwait(false);
                return result with { PublicationSucceeded = true };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (attempt < settings.MaxRetries && IsTransient(e))
            {
                await DelayWithBackoffAsync(attempt + 1, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                return result with { PublicationFailure = e.Message };
            }
        }
    }

    private async Task DelayWithBackoffAsync(int attempt, CancellationToken ct)
    {
        // Exponential backoff with small jitter: base * 2^(attempt-1) + 0..250ms
        var delayMs = settings.BaseRetryDelayMs * (1 << Math.Min(attempt - 1, 6));
        delayMs += Random.Shared.Next(0, 250);
        await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false);
    }

    private static bool IsTransient(Exception e) =>
        e is HttpRequestException or TimeoutException or TaskCanceledException { CancellationToken.IsCancellationRequested: false };

    private string? Truncate(string? body)
    {
        if (body is null) return null;
        if (body.Length <= settings.ResponseBodyLimit) return body;
        return body[..settings.ResponseBodyLimit] + " [truncated]";
    }

    /// <summary>Masks common secret key/value patterns and known sensitive JSON keys before persistence.</summary>
    private static string? Mask(string? value)
    {
        if (value is null) return null;
        var masked = SensitivePattern.Replace(value, "$1: ***");
        foreach (var key in SensitiveJsonKeys)
        {
            // Match "key": "secretvalue" without nested verbatim-string quote issues.
            var pattern = "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"[^\"]*\"";
            masked = Regex.Replace(masked, pattern, "\"" + key + "\": \"***\"", RegexOptions.IgnoreCase);
        }
        return masked;
    }
}
