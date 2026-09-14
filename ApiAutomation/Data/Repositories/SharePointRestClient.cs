using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ApiAutomation.Configuration;
using ApiAutomation.Core.Interfaces;
using ApiAutomation.Core.Models;

namespace ApiAutomation.Data.Repositories;

/// <summary>
/// Shared Microsoft Graph REST transport for SharePoint list repositories.
/// </summary>
/// <remarks>
/// This type deliberately owns transport concerns only: authentication headers, JSON serialization,
/// cancellation, and actionable HTTP failures. Repository classes own list schemas and business mapping.
/// </remarks>
public sealed class SharePointRestClient
{
    private readonly HttpClient _http;

    /// <summary>Creates an authenticated Graph client using a token resolved at runtime.</summary>
    /// <param name="settings">SharePoint resource settings containing the token secret name.</param>
    /// <param name="secrets">Runtime secret resolver; credentials are never read from list data.</param>
    /// <param name="http">Optional injected client, primarily for tests or centrally managed HTTP lifetime.</param>
    public SharePointRestClient(SharePointSettings settings, ISecretResolver secrets, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(secrets);
        var token = secrets.Resolve(settings.AccessTokenSecret);
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException($"Missing secret '{settings.AccessTokenSecret}'.");

        _http = http ?? new HttpClient { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Retrieves and parses a Graph JSON response.</summary>
    public async Task<JsonDocument> GetJsonAsync(string relativeOrAbsoluteUrl, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(relativeOrAbsoluteUrl, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Posts a JSON payload to Graph and verifies that the server accepted it.</summary>
    public Task PostJsonAsync(string relativeOrAbsoluteUrl, object payload, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Post, relativeOrAbsoluteUrl, payload, cancellationToken);

    /// <summary>Patches an existing Graph resource with a JSON payload.</summary>
    public Task PatchJsonAsync(string relativeOrAbsoluteUrl, object payload, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Patch, relativeOrAbsoluteUrl, payload, cancellationToken);

    /// <summary>Sends a JSON request with cancellation propagation and bounded diagnostic failures.</summary>
    private async Task SendJsonAsync(HttpMethod method, string relativeOrAbsoluteUrl, object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeOrAbsoluteUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonDefaults.Options), Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Converts non-success Graph responses into exceptions retaining status and safe bounded context.</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var excerpt = body[..Math.Min(body.Length, 1_000)];
        throw new HttpRequestException($"Microsoft Graph returned {(int)response.StatusCode} ({response.StatusCode}): {excerpt}", null, response.StatusCode);
    }
}
