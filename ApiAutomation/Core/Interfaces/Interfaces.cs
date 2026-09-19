using ApiAutomation.Core.Models;

namespace ApiAutomation.Core.Interfaces;

public interface ITestCaseProvider
{
    Task<IReadOnlyList<TestCaseDefinition>> GetAsync(ExecutionFilter filter, CancellationToken cancellationToken);
}

public interface IResultRepository
{
    Task CreateRunAsync(string executionId, ExecutionFilter filter, CancellationToken cancellationToken);
    Task PersistAsync(TestExecutionResult result, CancellationToken cancellationToken);
    Task CompleteRunAsync(ExecutionSummary summary, CancellationToken cancellationToken);
}

public interface IRequestBuilder
{
    ApiRequest Build(TestCaseDefinition testCase, EnvironmentConfiguration environment, string correlationId);
}

public interface IApiClient : IAsyncDisposable
{
    Task<ApiResponse> SendAsync(ApiRequest request, CancellationToken cancellationToken);
}

public interface IValidationEngine
{
    IReadOnlyList<ValidationResult> Validate(TestCaseDefinition testCase, ApiResponse response);
}

public interface IExecutionProgressSink
{
    void Report(TestExecutionResult result);
    void Complete(ExecutionSummary summary);
}

public interface ISecretResolver
{
    string? Resolve(string name);
}

/// <summary>
/// Resolves a Microsoft Graph (or other) access token at call time.
/// Implementations may use a static env var (dev) or MSAL client-credentials / certificate (production).
/// </summary>
public interface ITokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

public sealed record EnvironmentConfiguration(
    string Name,
    string BaseUrl,
    int TimeoutMs,
    string? BearerTokenSecret,
    IReadOnlyDictionary<string, string> DefaultHeaders);
