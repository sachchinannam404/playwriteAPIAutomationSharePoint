using System.Text.Json;
using ApiAutomation.Configuration;
using ApiAutomation.Core.Interfaces;
using ApiAutomation.Core.Models;
using ApiAutomation.Data.Repositories;

namespace ApiAutomation.Data.SharePoint;

/// <summary>Reads test definitions and publishes execution records through Microsoft Graph list endpoints.</summary>
/// <remarks>List item titles are correlation keys. Create-only persistence preserves historical attempts.</remarks>
public sealed class GraphSharePointRepository : ITestCaseProvider, IResultRepository
{
    private readonly SharePointRestClient _client;
    private readonly SharePointSettings _settings;

    /// <summary>Creates a Graph repository using an access token resolved only at runtime.</summary>
    public GraphSharePointRepository(SharePointSettings settings, ISecretResolver secrets, HttpClient? http = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _client = new SharePointRestClient(settings, secrets, http);
    }

    /// <summary>Gets every active definition matching the requested filters, following Graph pagination links.</summary>
    public async Task<IReadOnlyList<TestCaseDefinition>> GetAsync(ExecutionFilter filter, CancellationToken cancellationToken)
    {
        var fields = Uri.EscapeDataString("fields($select=Title,Suite,ApiName,Endpoint,HttpMethod,Scenario,Active,Priority,ExpectedStatusCode,Headers,QueryParameters,PathParameters,Payload,ValidationRules,Authentication,SchemaVersion)");
        var url = $"sites/{_settings.SiteId}/lists/{_settings.TestCasesListId}/items?$expand={fields}";
        var cases = new List<TestCaseDefinition>();

        // Graph list responses are paged; consuming nextLink prevents silently omitting large suites.
        while (!string.IsNullOrWhiteSpace(url))
        {
            using var document = await _client.GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            cases.AddRange(document.RootElement.GetProperty("value").EnumerateArray().Select(ParseTestCase));
            url = document.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }

        return cases.Where(x => x.Active).Where(x => Matches(x, filter)).OrderBy(x => x.Priority).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Creates the run's initial, independently auditable <c>Running</c> list record.</summary>
    public Task CreateRunAsync(string executionId, ExecutionFilter filter, CancellationToken cancellationToken) =>
        PostAsync(_settings.RunsListId, new { fields = new { Title = executionId, ExecutionId = executionId, Environment = filter.Environment, Status = "Running", StartedAt = DateTimeOffset.UtcNow } }, cancellationToken);

    /// <summary>Writes a completion snapshot for the run. Retention jobs can collapse these snapshots if required.</summary>
    public Task CompleteRunAsync(ExecutionSummary summary, CancellationToken cancellationToken) =>
        PostAsync(_settings.RunsListId, new { fields = new { Title = $"{summary.ExecutionId}:complete", ExecutionId = summary.ExecutionId, Status = summary.Failed == 0 ? "Passed" : "Failed", CompletedAt = summary.CompletedAt, Total = summary.Results.Count, Passed = summary.Passed, Failed = summary.Failed } }, cancellationToken);

    /// <summary>Writes one immutable execution attempt whose title is the deterministic correlation identifier.</summary>
    public Task PersistAsync(TestExecutionResult result, CancellationToken cancellationToken) =>
        PostAsync(_settings.ExecutionsListId, new { fields = new { Title = result.CorrelationId, result.ExecutionId, TestCaseId = result.TestCaseId, Attempt = result.Attempt, result.Environment, result.ApiName, result.Scenario, HttpMethod = result.Method, result.Endpoint, Status = result.Status.ToString(), ActualStatusCode = result.ActualStatusCode, result.ExpectedStatusCode, ValidationStatus = result.Validations.All(x => x.Passed) ? "Passed" : "Failed", ValidationSummary = string.Join("; ", result.Validations.Where(x => !x.Passed).Select(x => x.FailureMessage)), result.FailureReason, ResponsePayload = result.ResponsePayload, DurationMs = (long)result.Duration.TotalMilliseconds, result.RetryCount, result.StartedAt, result.CompletedAt } }, cancellationToken);

    /// <summary>Posts a list item and converts Graph failures into transient-aware HTTP exceptions with response context.</summary>
    private async Task PostAsync(string listId, object payload, CancellationToken cancellationToken)
    {
        await _client.PostJsonAsync($"sites/{_settings.SiteId}/lists/{listId}/items", payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maps a Graph list item into the version-tolerant test-definition model.</summary>
    private static TestCaseDefinition ParseTestCase(JsonElement item)
    {
        var fields = item.GetProperty("fields");
        string? TextOrNull(string name) => fields.TryGetProperty(name, out var value) ? value.ToString() : null;
        string Text(string name) => TextOrNull(name) ?? string.Empty;
        T Json<T>(string name, T fallback)
        {
            var raw = TextOrNull(name);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            try { return JsonSerializer.Deserialize<T>(raw, JsonDefaults.Options) ?? fallback; }
            catch (JsonException error) { throw new InvalidOperationException($"Test case '{Text("Title")}' has invalid {name}: {error.Message}", error); }
        }

        return new(Text("Title"), Text("Suite"), Text("ApiName"), Text("Endpoint"), Text("HttpMethod"), Text("Scenario"), bool.TryParse(Text("Active"), out var active) && active, int.TryParse(Text("Priority"), out var priority) ? priority : 0, int.TryParse(Text("ExpectedStatusCode"), out var status) ? status : 0, Json("Headers", new Dictionary<string, string>()), Json("QueryParameters", new Dictionary<string, string>()), Json("PathParameters", new Dictionary<string, string>()), TextOrNull("Payload"), Json("ValidationRules", new List<ValidationRule>()), TextOrNull("Authentication"), int.TryParse(Text("SchemaVersion"), out var schemaVersion) ? schemaVersion : 1);
    }

    /// <summary>Applies local filters after retrieval so the source schema remains simple and backward compatible.</summary>
    private static bool Matches(TestCaseDefinition test, ExecutionFilter filter) =>
        (string.IsNullOrWhiteSpace(filter.Suite) || test.Suite.Equals(filter.Suite, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(filter.Api) || test.ApiName.Equals(filter.Api, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(filter.Scenario) || test.Scenario.Equals(filter.Scenario, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(filter.TestCaseId) || test.Id.Equals(filter.TestCaseId, StringComparison.OrdinalIgnoreCase));
}
