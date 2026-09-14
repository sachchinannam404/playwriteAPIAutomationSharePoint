using ApiAutomation.Application;
using ApiAutomation.Configuration;
using ApiAutomation.Core.Interfaces;
using ApiAutomation.Core.Validation;
using ApiAutomation.Data.SharePoint;
using ApiAutomation.GUI;
using ApiAutomation.Playwright;

// Parse command-line options here; the engine remains reusable by desktop, web, and pipeline hosts.
if (args.Contains("--help", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine("Usage: ApiAutomation --environment <name> [--suite <name>] [--api <name>] [--scenario <name>] [--test-case <id>] [--parallelism <n>] [--fail-fast]");
    return 0;
}
try
{
    var options = Parse(args);
    if (!options.TryGetValue("environment", out var environment) || string.IsNullOrWhiteSpace(environment)) throw new ArgumentException("--environment is required. Use --help for usage.");
    var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    var settings = FrameworkSettings.Load(settingsPath);
    var filter = new ApiAutomation.Core.Models.ExecutionFilter(environment, Get("suite"), Get("api"), Get("scenario"), Get("test-case"), int.TryParse(Get("parallelism"), out var parallelism) ? Math.Max(1, parallelism) : 1, options.ContainsKey("fail-fast"));
    ISecretResolver secrets = new EnvironmentSecretResolver();
    var repository = new GraphSharePointRepository(settings.SharePoint, secrets);
    await using IApiClient api = new PlaywrightApiClient();
    var engine = new ExecutionEngine(repository, repository, new DynamicRequestBuilder(secrets), api, new JsonValidationEngine(), settings, new ConsoleProgressSink());
    var summary = await engine.ExecuteAsync(filter, CancellationToken.None);
    return summary.Failed > 0 || summary.PublicationFailures > 0 ? 1 : 0;

    string? Get(string name) => options.TryGetValue(name, out var value) ? value : null;
}
catch (Exception e)
{
    Console.Error.WriteLine($"Framework error: {e.Message}");
    return 2;
}

// Converts --name value command-line pairs into a case-insensitive option map.
static Dictionary<string, string?> Parse(string[] values)
{
    var output = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < values.Length; i++)
    {
        if (!values[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument '{values[i]}'.");
        var key = values[i][2..];
        output[key] = i + 1 < values.Length && !values[i + 1].StartsWith("--", StringComparison.Ordinal) ? values[++i] : null;
    }
    return output;
}
