using ApiAutomation.Core.Interfaces;
using ApiAutomation.Core.Models;
using Microsoft.Playwright;

namespace ApiAutomation.Playwright;

/// <summary>Builds an isolated API request from external data without embedding test-specific behavior in code.</summary>
public sealed class DynamicRequestBuilder(ISecretResolver secrets) : IRequestBuilder
{
    /// <summary>Resolves the endpoint, path/query parameters, configured headers, and runtime bearer token.</summary>
    public ApiRequest Build(TestCaseDefinition testCase, EnvironmentConfiguration environment, string correlationId)
    {
        var endpoint = testCase.Endpoint;
        foreach (var item in testCase.PathParameters) endpoint = endpoint.Replace($"{{{item.Key}}}", Uri.EscapeDataString(item.Value), StringComparison.Ordinal);
        if (!Uri.TryCreate(new Uri(environment.BaseUrl.TrimEnd('/') + "/"), endpoint.TrimStart('/'), out var url)) throw new InvalidOperationException($"Invalid endpoint '{testCase.Endpoint}'.");
        var query = string.Join("&", testCase.QueryParameters.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
        if (!string.IsNullOrWhiteSpace(query)) url = new UriBuilder(url) { Query = query }.Uri;
        var headers = new Dictionary<string, string>(environment.DefaultHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var header in testCase.Headers) headers[header.Key] = header.Value;
        headers["X-Correlation-ID"] = correlationId;
        if (!string.IsNullOrWhiteSpace(environment.BearerTokenSecret))
        {
            var token = secrets.Resolve(environment.BearerTokenSecret);
            if (!string.IsNullOrWhiteSpace(token)) headers["Authorization"] = $"Bearer {token}";
        }
        return new ApiRequest(testCase.Method.ToUpperInvariant(), url, headers, testCase.Payload, environment.TimeoutMs);
    }
}

/// <summary>Executes a single HTTP request through Playwright and returns a transport-neutral response model.</summary>
public sealed class PlaywrightApiClient : IApiClient
{
    private readonly IPlaywright _playwright = Microsoft.Playwright.Playwright.CreateAsync().GetAwaiter().GetResult();
    /// <summary>Creates a per-call Playwright context so timeout and headers cannot leak between test cases.</summary>
    public async Task<ApiResponse> SendAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        var options = new APIRequestNewContextOptions { ExtraHTTPHeaders = request.Headers, Timeout = request.TimeoutMs, FailOnStatusCode = false };
        // A short-lived context lets headers and timeout vary safely per SharePoint record.
        await using var context = await _playwright.APIRequest.NewContextAsync(options).ConfigureAwait(false);
        var response = await context.FetchAsync(request.Url.ToString(), new() { Method = request.Method, Data = request.Payload }).WaitAsync(cancellationToken).ConfigureAwait(false);
        var headers = await response.AllHeadersAsync().ConfigureAwait(false);
        return new ApiResponse(response.Status, headers, await response.TextAsync().ConfigureAwait(false));
    }
    /// <summary>Releases Playwright resources after the execution run finishes.</summary>
    public ValueTask DisposeAsync() { _playwright.Dispose(); return ValueTask.CompletedTask; }
}
