using System.Text.Json;
using ApiAutomation.Core.Interfaces;

namespace ApiAutomation.Configuration;

/// <summary>Runtime settings loaded from JSON; secret values are resolved separately from the process environment.</summary>
public sealed class FrameworkSettings
{
    public Dictionary<string, EnvironmentConfiguration> Environments { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public SharePointSettings SharePoint { get; init; } = new();
    public int MaxRetries { get; init; } = 2;
    public int ResponseBodyLimit { get; init; } = 65_000;
    /// <summary>Base delay in milliseconds for exponential backoff (delay = BaseRetryDelayMs * 2^attempt + jitter).</summary>
    public int BaseRetryDelayMs { get; init; } = 500;
    /// <summary>HTTP status codes that should be treated as transient and retried (in addition to transport exceptions).</summary>
    public int[] RetryableStatusCodes { get; init; } = [408, 429, 500, 502, 503, 504];
    /// <summary>When true, result writes are buffered and flushed in batches (see BatchSize).</summary>
    public bool EnableResultBatching { get; init; } = false;
    /// <summary>Maximum buffered results before an automatic flush. Only used when EnableResultBatching is true.</summary>
    public int BatchSize { get; init; } = 25;

    public static FrameworkSettings Load(string path) =>
        JsonSerializer.Deserialize<FrameworkSettings>(File.ReadAllText(path), Core.Models.JsonDefaults.Options)
        ?? throw new InvalidOperationException("Settings file is empty.");
}

/// <summary>Identifies Graph resources; deliberately contains no credential values.</summary>
public sealed class SharePointSettings
{
    public string SiteId { get; init; } = "";
    public string TestCasesListId { get; init; } = "";
    public string ExecutionsListId { get; init; } = "";
    public string RunsListId { get; init; } = "";
    public string AccessTokenSecret { get; init; } = "SHAREPOINT_ACCESS_TOKEN";
    /// <summary>
    /// When true (default), request Active eq 1 server-side via OData $filter.
    /// Set false if the Active column type does not support that filter expression;
    /// all items are then loaded and filtered client-side.
    /// </summary>
    public bool UseServerSideActiveFilter { get; init; } = true;
    /// <summary>
    /// OData filter fragment applied when UseServerSideActiveFilter is true.
    /// Default assumes a Yes/No or number column stored as 1 for active.
    /// Alternatives: "fields/Active eq true" or "fields/Active eq 'Yes'".
    /// </summary>
    public string ActiveFilterExpression { get; init; } = "fields/Active eq 1";
}

/// <summary>Resolves secrets from environment variables (local/dev and pipeline).</summary>
public sealed class EnvironmentSecretResolver : ISecretResolver
{
    public string? Resolve(string name) => Environment.GetEnvironmentVariable(name);
}

/// <summary>
/// Dev/pipeline token provider that reads a static bearer token from an environment variable.
/// Prefer MsalClientCredentialsTokenProvider (certificate or client secret) for production.
/// </summary>
public sealed class EnvironmentTokenProvider(ISecretResolver secrets, string secretName) : ITokenProvider
{
    private string? _cached;

    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
            return Task.FromResult(_cached);

        var token = secrets.Resolve(secretName);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                $"Missing access token secret '{secretName}'. Set the environment variable or use a production ITokenProvider.");
        _cached = token;
        return Task.FromResult(_cached);
    }

    public void Invalidate() => _cached = null;
}

/// <summary>
/// Production token provider using MSAL confidential client (client credentials).
/// Supports client secret or certificate. MSAL cache handles expiry; Invalidate forces re-acquisition.
/// </summary>
public sealed class MsalClientCredentialsTokenProvider : ITokenProvider
{
    private readonly Microsoft.Identity.Client.IConfidentialClientApplication _app;
    private readonly string[] _scopes;
    private readonly object _gate = new();
    private Microsoft.Identity.Client.AuthenticationResult? _last;

    public MsalClientCredentialsTokenProvider(
        string tenantId,
        string clientId,
        string clientSecret,
        string[]? scopes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);

        _scopes = scopes is { Length: > 0 } ? scopes : ["https://graph.microsoft.com/.default"];
        _app = Microsoft.Identity.Client.ConfidentialClientApplicationBuilder
            .Create(clientId)
            .WithClientSecret(clientSecret)
            .WithAuthority(Microsoft.Identity.Client.AzureCloudInstance.AzurePublic, tenantId)
            .Build();
    }

    public MsalClientCredentialsTokenProvider(
        string tenantId,
        string clientId,
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate,
        string[]? scopes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(certificate);

        _scopes = scopes is { Length: > 0 } ? scopes : ["https://graph.microsoft.com/.default"];
        _app = Microsoft.Identity.Client.ConfidentialClientApplicationBuilder
            .Create(clientId)
            .WithCertificate(certificate)
            .WithAuthority(Microsoft.Identity.Client.AzureCloudInstance.AzurePublic, tenantId)
            .Build();
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_last is not null && _last.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(2))
                return _last.AccessToken;
        }

        var result = await _app.AcquireTokenForClient(_scopes)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        lock (_gate) _last = result;
        return result.AccessToken;
    }

    public void Invalidate()
    {
        lock (_gate) _last = null;
    }
}
