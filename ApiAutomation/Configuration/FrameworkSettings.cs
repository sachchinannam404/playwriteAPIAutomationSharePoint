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
}

/// <summary>Resolves secrets from environment variables (local/dev and pipeline).</summary>
public sealed class EnvironmentSecretResolver : ISecretResolver
{
    public string? Resolve(string name) => Environment.GetEnvironmentVariable(name);
}

/// <summary>
/// Dev/pipeline token provider that reads a static bearer token from an environment variable.
/// Prefer a MSAL-based implementation (certificate or client secret) for production.
/// </summary>
public sealed class EnvironmentTokenProvider(ISecretResolver secrets, string secretName) : ITokenProvider
{
    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var token = secrets.Resolve(secretName);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException($"Missing access token secret '{secretName}'. Set the environment variable or use a production ITokenProvider.");
        return Task.FromResult(token);
    }
}
