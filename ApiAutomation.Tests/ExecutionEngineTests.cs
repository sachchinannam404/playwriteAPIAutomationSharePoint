using ApiAutomation.Application;
using ApiAutomation.Configuration;
using ApiAutomation.Core.Interfaces;
using ApiAutomation.Core.Models;
using ApiAutomation.Core.Validation;
using Xunit;

namespace ApiAutomation.Tests;

public sealed class ExecutionEngineTests
{
    [Fact]
    public async Task Marks_invalid_data_without_sending_an_api_request_and_publishes_it()
    {
        var api = new FakeApiClient(new ApiResponse(200, new Dictionary<string, string>(), "{}"));
        var repository = new FakeRepository([TestCase(method: "TRACE")]);
        var engine = CreateEngine(repository, api);

        var summary = await engine.ExecuteAsync(new("qa"), CancellationToken.None);

        var result = Assert.Single(summary.Results);
        Assert.Equal(ExecutionStatus.Invalid, result.Status);
        Assert.Equal(0, api.Calls);
        Assert.True(result.PublicationSucceeded);
        Assert.Single(repository.Persisted);
    }

    [Fact]
    public async Task Retries_transient_api_error_and_persists_the_successful_attempt()
    {
        var api = new FakeApiClient(new HttpRequestException("temporary"), new ApiResponse(200, new Dictionary<string, string>(), "{}"));
        var repository = new FakeRepository([TestCase()]);
        var engine = CreateEngine(repository, api);

        var summary = await engine.ExecuteAsync(new("qa"), CancellationToken.None);

        var result = Assert.Single(summary.Results);
        Assert.Equal(ExecutionStatus.Passed, result.Status);
        Assert.Equal(2, api.Calls);
        Assert.Equal(1, result.RetryCount);
        Assert.True(result.PublicationSucceeded);
    }

    [Fact]
    public async Task Retries_configured_status_codes_like_429()
    {
        var api = new FakeApiClient(
            new ApiResponse(429, new Dictionary<string, string>(), "throttled"),
            new ApiResponse(200, new Dictionary<string, string>(), "{}"));
        var repository = new FakeRepository([TestCase()]);
        var engine = CreateEngine(repository, api);

        var summary = await engine.ExecuteAsync(new("qa"), CancellationToken.None);

        var result = Assert.Single(summary.Results);
        Assert.Equal(ExecutionStatus.Passed, result.Status);
        Assert.Equal(2, api.Calls);
        Assert.Equal(1, result.RetryCount);
    }

    [Fact]
    public async Task Separates_result_publication_failure_from_api_failure()
    {
        var repository = new FakeRepository([TestCase()]) { FailPersist = true };
        var engine = CreateEngine(repository, new FakeApiClient(new ApiResponse(200, new Dictionary<string, string>(), "{}")));

        var summary = await engine.ExecuteAsync(new("qa"), CancellationToken.None);

        var result = Assert.Single(summary.Results);
        Assert.Equal(ExecutionStatus.Passed, result.Status);
        Assert.False(result.PublicationSucceeded);
        Assert.NotNull(result.PublicationFailure);
        Assert.Equal(1, summary.PublicationFailures);
    }

    private static ExecutionEngine CreateEngine(FakeRepository repository, FakeApiClient api) =>
        new(repository, repository, new FixedRequestBuilder(), api, new JsonValidationEngine(),
            new FrameworkSettings
            {
                MaxRetries = 1,
                BaseRetryDelayMs = 1,
                RetryableStatusCodes = [408, 429, 500, 502, 503, 504],
                Environments = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["qa"] = new("qa", "https://api.example.test", 100, null, new Dictionary<string, string>())
                }
            },
            new NullProgressSink());

    private static TestCaseDefinition TestCase(string method = "GET") =>
        new("TC-1", "Smoke", "API", "/health", method, "health", true, 1, 200,
            new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>(), null, []);

    private sealed class FixedRequestBuilder : IRequestBuilder
    {
        public ApiRequest Build(TestCaseDefinition testCase, EnvironmentConfiguration environment, string correlationId) =>
            new(testCase.Method, new Uri("https://api.example.test/health"), new Dictionary<string, string>(), testCase.Payload, 100);
    }

    private sealed class NullProgressSink : IExecutionProgressSink
    {
        public void Complete(ExecutionSummary summary) { }
        public void Report(TestExecutionResult result) { }
    }

    private sealed class FakeApiClient(params object[] outcomes) : IApiClient
    {
        private readonly Queue<object> _outcomes = new(outcomes);
        public int Calls { get; private set; }

        public Task<ApiResponse> SendAsync(ApiRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            var outcome = _outcomes.Dequeue();
            return outcome is Exception error
                ? Task.FromException<ApiResponse>(error)
                : Task.FromResult((ApiResponse)outcome);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRepository(IReadOnlyList<TestCaseDefinition> definitions) : ITestCaseProvider, IResultRepository
    {
        public bool FailPersist { get; init; }
        public List<TestExecutionResult> Persisted { get; } = [];

        public Task CompleteRunAsync(ExecutionSummary summary, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CreateRunAsync(string executionId, ExecutionFilter filter, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<TestCaseDefinition>> GetAsync(ExecutionFilter filter, CancellationToken cancellationToken) => Task.FromResult(definitions);

        public Task PersistAsync(TestExecutionResult result, CancellationToken cancellationToken)
        {
            if (FailPersist) throw new InvalidOperationException("SharePoint unavailable");
            Persisted.Add(result);
            return Task.CompletedTask;
        }
    }
}
