using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ApiAutomation.Configuration;
using ApiAutomation.Core.Interfaces;
using ApiAutomation.Core.Models;
using Microsoft.Extensions.Logging;

namespace ApiAutomation.Data.Repositories;

/// <summary>
/// Shared Microsoft Graph REST transport for SharePoint list repositories.
/// Owns transport concerns only: authentication headers, JSON serialization, cancellation,
/// correlation headers, 401 token refresh, and actionable HTTP failures.
/// Tokens are not cached indefinitely; a 401 triggers Invalidate + one retry with a fresh token.
/// </summary>
public sealed class SharePointRestClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ITokenProvider _tokenProvider;
    private readonly ILogger _log;
    private string? _requestCorrelationId;

    public SharePointRestClient(SharePointSettings settings, ITokenProvider tokenProvider, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _tokenProvider = tokenProvider;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        if (!_http.DefaultRequestHeaders.Accept.Any())
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _log = FrameworkLogging.CreateLogger(nameof(SharePointRestClient));
    }

    public SharePointRestClient(SharePointSettings settings, ISecretResolver secrets, HttpClient? http = null)
        : this(settings, new EnvironmentTokenProvider(secrets, settings.AccessTokenSecret), http)
    {
    }

    /// <summary>Optional correlation ID applied to subsequent Graph requests (client-request-id / X-Correlation-ID).</summary>
    public void SetCorrelationId(string? correlationId) => _requestCorrelationId = correlationId;

    public async Task<JsonDocument> GetJsonAsync(string relativeOrAbsoluteUrl, CancellationToken cancellationToken)
    {
        using var response = await SendWithAuthRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, relativeOrAbsoluteUrl),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
    }

    public Task PostJsonAsync(string relativeOrAbsoluteUrl, object payload, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Post, relativeOrAbsoluteUrl, payload, cancellationToken);

    public Task PatchJsonAsync(string relativeOrAbsoluteUrl, object payload, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Patch, relativeOrAbsoluteUrl, payload, cancellationToken);

    private async Task SendJsonAsync(HttpMethod method, string relativeOrAbsoluteUrl, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonDefaults.Options);
        using var response = await SendWithAuthRetryAsync(
            () => new HttpRequestMessage(method, relativeOrAbsoluteUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            },
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the request with a fresh token. On 401, invalidates the provider and retries once.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithAuthRetryAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = createRequest();
            await ApplyAuthAsync(request, cancellationToken).ConfigureAwait(false);
            ApplyCorrelation(request);

            _log.LogDebug("Graph {Method} {Url} attempt={Attempt} correlation={Correlation}",
                request.Method, request.RequestUri, attempt + 1, _requestCorrelationId);

            var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                _log.LogWarning("Graph returned 401; invalidating token and retrying once. correlation={Correlation}",
                    _requestCorrelationId);
                response.Dispose();
                _tokenProvider.Invalidate();
                continue;
            }

            return response;
        }

        throw new InvalidOperationException("Unreachable auth-retry state.");
    }

    private async Task ApplyAuthAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private void ApplyCorrelation(HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(_requestCorrelationId)) return;
        request.Headers.TryAddWithoutValidation("client-request-id", _requestCorrelationId);
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", _requestCorrelationId);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var excerpt = body.Length <= 1_000 ? body : body[..1_000];
        throw new HttpRequestException(
            $"Microsoft Graph returned {(int)response.StatusCode} ({response.StatusCode}): {excerpt}",
            null,
            response.StatusCode);
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttp) _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
