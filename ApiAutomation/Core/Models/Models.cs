using System.Text.Json;

namespace ApiAutomation.Core.Models;

public sealed record TestCaseDefinition(
    string Id, string Suite, string ApiName, string Endpoint, string Method, string Scenario,
    bool Active, int Priority, int ExpectedStatusCode, IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> QueryParameters, IReadOnlyDictionary<string, string> PathParameters,
    string? Payload, IReadOnlyList<ValidationRule> ValidationRules, string? Authentication = null,
    int SchemaVersion = 1);

public sealed record ValidationRule(string Id, string Type, string? Path, string? ExpectedValue, string? Operator = null);
public sealed record ApiRequest(string Method, Uri Url, IReadOnlyDictionary<string, string> Headers, string? Payload, int TimeoutMs);
public sealed record ApiResponse(int StatusCode, IReadOnlyDictionary<string, string> Headers, string Body);
public sealed record ValidationResult(string RuleId, bool Passed, string Expected, string? Actual, string? FailureMessage);
public enum ExecutionStatus { Passed, Failed, Skipped, Blocked, Error, Invalid }

public sealed record TestExecutionResult(
    string ExecutionId, string TestCaseId, int Attempt, string Environment, string ApiName, string Scenario,
    string Method, string Endpoint, ExecutionStatus Status, int? ActualStatusCode, int ExpectedStatusCode,
    IReadOnlyList<ValidationResult> Validations, string? FailureReason, string? RequestPayload,
    string? ResponsePayload, TimeSpan Duration, int RetryCount, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    bool PublicationSucceeded = false, string? PublicationFailure = null)
{
    public string CorrelationId => $"{ExecutionId}:{TestCaseId}:{Attempt}";
}

public sealed record ExecutionFilter(string Environment, string? Suite = null, string? Api = null,
    string? Scenario = null, string? TestCaseId = null, int Parallelism = 1, bool FailFast = false);
public sealed record ExecutionSummary(string ExecutionId, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    IReadOnlyList<TestExecutionResult> Results)
{
    public int Passed => Results.Count(x => x.Status == ExecutionStatus.Passed);
    public int Failed => Results.Count(x => x.Status is ExecutionStatus.Failed or ExecutionStatus.Error or ExecutionStatus.Invalid);
    public int PublicationFailures => Results.Count(x => !x.PublicationSucceeded);
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
}
