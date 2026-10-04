using ApiAutomation.Core.Models;
using Microsoft.Extensions.Logging;

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

/// <summary>
/// Optional batching seam for high-volume result writes. Implementations may buffer and flush
/// on CompleteRunAsync or when the batch size is reached.
/// </summary>
public interface IBatchedResultRepository : IResultRepository
{
    Task FlushAsync(CancellationToken cancellationToken);
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
/// Tokens must not be cached indefinitely; prefer short-lived acquisition or refresh-on-401.
/// </summary>
public interface ITokenProvider
{
    /// <summary>Returns a valid access token. Callers may invoke this on every request or after 401.</summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates any cached token so the next GetAccessTokenAsync acquires a fresh one.
    /// Used after Graph returns 401 Unauthorized.
    /// </summary>
    void Invalidate();
}

/// <summary>Factory for optional structured logging; keeps the engine free of a hard DI container.</summary>
public static class FrameworkLogging
{
    public static ILoggerFactory? Factory { get; set; }

    public static ILogger CreateLogger(string category) =>
        Factory?.CreateLogger(category) ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public static ILogger<T> CreateLogger<T>() =>
        Factory?.CreateLogger<T>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;
}

public sealed record EnvironmentConfiguration(
    string Name,
    string BaseUrl,
    int TimeoutMs,
    string? BearerTokenSecret,
    IReadOnlyDictionary<string, string> DefaultHeaders);
