using System.Text.Json;
using ApiAutomation.Core.Interfaces;

namespace ApiAutomation.Configuration;

/// <summary>Runtime settings loaded from JSON, with secret values resolved separately from the process environment.</summary>
public sealed class FrameworkSettings
{
    public Dictionary<string, EnvironmentConfiguration> Environments { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public SharePointSettings SharePoint { get; init; } = new();
    public int MaxRetries { get; init; } = 2;
    public int ResponseBodyLimit { get; init; } = 65_000;
    /// <summary>Loads immutable runtime configuration from the specified JSON file.</summary>
    public static FrameworkSettings Load(string path) => JsonSerializer.Deserialize<FrameworkSettings>(File.ReadAllText(path), Core.Models.JsonDefaults.Options) ?? throw new InvalidOperationException("Settings file is empty.");
}
/// <summary>Identifies Graph resources; it deliberately contains no credential values.</summary>
public sealed class SharePointSettings
{
    public string SiteId { get; init; } = "";
    public string TestCasesListId { get; init; } = "";
    public string ExecutionsListId { get; init; } = "";
    public string RunsListId { get; init; } = "";
    public string AccessTokenSecret { get; init; } = "SHAREPOINT_ACCESS_TOKEN";
}
/// <summary>Resolves approved local or pipeline secrets from environment variables.</summary>
public sealed class EnvironmentSecretResolver : ISecretResolver { public string? Resolve(string name) => Environment.GetEnvironmentVariable(name); }
