using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ApiAutomation.Configuration;
using ApiAutomation.Core.Interfaces;
using ApiAutomation.Core.Models;

namespace ApiAutomation.Data.Repositories;

/// <summary>
/// Shared Microsoft Graph REST transport for SharePoint list repositories.
/// Owns transport concerns only: authentication headers, JSON serialization, cancellation, and actionable HTTP failures.
/// </summary>
public sealed class SharePointRestClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ITokenProvider _tokenProvider;
    private string? _cachedToken;

    /// <summary>Creates an authenticated Graph client. Token is resolved lazily on first request and can be refreshed by the provider.</summary>
    public SharePointRestClient(SharePointSettings settings, ITokenProvider tokenProvider, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _tokenProvider = tokenProvider;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Backward-compatible constructor that wraps a secret name as an EnvironmentTokenProvider.</summary>
    public SharePointRestClient(SharePointSettings settings, ISecretResolver secrets, HttpClient? http = null)
        : this(settings, new EnvironmentTokenProvider(secrets, settings.AccessTokenSecret), http)
    {
    }

    public async Task<JsonDocument> GetJsonAsync(string relativeOrAbsoluteUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeOrAbsoluteUrl);
        await ApplyAuthAsync(request, cancellationToken).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
    }

    public Task PostJsonAsync(string relativeOrAbsoluteUrl, object payload, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Post, relativeOrAbsoluteUrl, payload, cancellationToken);

    public Task PatchJsonAsync(string relativeOrAbsoluteUrl, object payload, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Patch, relativeOrAbsoluteUrl, payload, cancellationToken);

    private async Task SendJsonAsync(HttpMethod method, string relativeOrAbsoluteUrl, object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeOrAbsoluteUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonDefaults.Options), Encoding.UTF8, "application/json")
        };
        await ApplyAuthAsync(request, cancellationToken).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyAuthAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _cachedToken ??= await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cachedToken);
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
