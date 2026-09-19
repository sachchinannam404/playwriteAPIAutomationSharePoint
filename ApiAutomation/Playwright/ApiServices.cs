using System.Text.RegularExpressions;
using ApiAutomation.Core.Interfaces;
using ApiAutomation.Core.Models;
using Microsoft.Playwright;

namespace ApiAutomation.Playwright;

/// <summary>Builds an isolated API request from external data without embedding test-specific behavior in code.</summary>
public sealed class DynamicRequestBuilder(ISecretResolver secrets) : IRequestBuilder
{
    private static readonly Regex SecretPlaceholder = new(@"\{\{secret:([A-Za-z0-9_]+)\}\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Resolves endpoint (absolute or relative), path/query parameters, headers (including {{secret:NAME}} placeholders),
    /// and the environment bearer token.
    /// </summary>
    public ApiRequest Build(TestCaseDefinition testCase, EnvironmentConfiguration environment, string correlationId)
    {
        var endpoint = testCase.Endpoint;
        foreach (var item in testCase.PathParameters)
            endpoint = endpoint.Replace($"{{{item.Key}}}", Uri.EscapeDataString(item.Value), StringComparison.Ordinal);

        Uri url;
        // Only treat as absolute when an HTTP(S) scheme is present; leading "/" is relative on all platforms.
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var absolute)
            && absolute.Scheme is "http" or "https")
        {
            url = absolute;
        }
        else
        {
            var baseUri = new Uri(environment.BaseUrl.TrimEnd('/') + "/");
            if (!Uri.TryCreate(baseUri, endpoint.TrimStart('/'), out url!))
                throw new InvalidOperationException($"Invalid endpoint '{testCase.Endpoint}' relative to base '{environment.BaseUrl}'.");
        }

        var query = string.Join("&", testCase.QueryParameters.Select(x =>
            $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(ResolveSecrets(x.Value))}"));
        if (!string.IsNullOrWhiteSpace(query))
            url = new UriBuilder(url) { Query = query }.Uri;

        var headers = new Dictionary<string, string>(environment.DefaultHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var header in testCase.Headers)
            headers[header.Key] = ResolveSecrets(header.Value);

        headers["X-Correlation-ID"] = correlationId;

        if (!string.IsNullOrWhiteSpace(environment.BearerTokenSecret))
        {
            var token = secrets.Resolve(environment.BearerTokenSecret);
            if (!string.IsNullOrWhiteSpace(token))
                headers["Authorization"] = $"Bearer {token}";
        }

        var payload = testCase.Payload is null ? null : ResolveSecrets(testCase.Payload);
        return new ApiRequest(testCase.Method.ToUpperInvariant(), url, headers, payload, environment.TimeoutMs);
    }

    private string ResolveSecrets(string value) =>
        SecretPlaceholder.Replace(value, match =>
        {
            var name = match.Groups[1].Value;
            var resolved = secrets.Resolve(name);
            return string.IsNullOrEmpty(resolved)
                ? throw new InvalidOperationException($"Secret '{name}' referenced in test data was not found.")
                : resolved;
        });
}

/// <summary>Executes a single HTTP request through Playwright and returns a transport-neutral response model.</summary>
public sealed class PlaywrightApiClient : IApiClient
{
    private readonly Lazy<Task<IPlaywright>> _playwright = new(() => Microsoft.Playwright.Playwright.CreateAsync());
    private IPlaywright? _instance;

    /// <summary>Creates a per-call Playwright context so timeout and headers cannot leak between test cases.</summary>
    public async Task<ApiResponse> SendAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        _instance ??= await _playwright.Value.ConfigureAwait(false);
        var options = new APIRequestNewContextOptions
        {
            ExtraHTTPHeaders = request.Headers,
            Timeout = request.TimeoutMs,
            FailOnStatusCode = false
        };

        await using var context = await _instance.APIRequest.NewContextAsync(options).ConfigureAwait(false);
        var response = await context
            .FetchAsync(request.Url.ToString(), new() { Method = request.Method, Data = request.Payload })
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ApiResponse(response.Status, response.Headers, await response.TextAsync().ConfigureAwait(false));
    }

    public ValueTask DisposeAsync()
    {
        _instance?.Dispose();
        return ValueTask.CompletedTask;
    }
}
