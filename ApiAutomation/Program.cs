using ApiAutomation.Application;
using ApiAutomation.Configuration;
using ApiAutomation.Core.Interfaces;
using ApiAutomation.Core.Models;
using ApiAutomation.Core.Validation;
using ApiAutomation.Data.SharePoint;
using ApiAutomation.GUI;
using ApiAutomation.Playwright;

if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
{
    PrintHelp();
    return 0;
}

try
{
    var options = Parse(args);
    if (!options.TryGetValue("environment", out var environment) || string.IsNullOrWhiteSpace(environment))
        throw new ArgumentException("--environment is required. Use --help for usage.");

    var settingsPath = options.TryGetValue("config", out var configPath) && !string.IsNullOrWhiteSpace(configPath)
        ? configPath
        : Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    if (!File.Exists(settingsPath))
        throw new FileNotFoundException($"Settings file not found: {settingsPath}");

    var settings = FrameworkSettings.Load(settingsPath);

    var parallelism = 1;
    if (options.TryGetValue("parallelism", out var pRaw) && !string.IsNullOrWhiteSpace(pRaw))
    {
        if (!int.TryParse(pRaw, out parallelism) || parallelism < 1)
            throw new ArgumentException("--parallelism must be a positive integer.");
    }

    var filter = new ExecutionFilter(
        environment,
        Get("suite"),
        Get("api"),
        Get("scenario"),
        Get("test-case"),
        parallelism,
        options.ContainsKey("fail-fast"));

    ISecretResolver secrets = new EnvironmentSecretResolver();
    ITokenProvider tokenProvider = new EnvironmentTokenProvider(secrets, settings.SharePoint.AccessTokenSecret);

    await using var repository = new GraphSharePointRepository(settings.SharePoint, tokenProvider);
    await using IApiClient api = new PlaywrightApiClient();

    var engine = new ExecutionEngine(
        repository,
        repository,
        new DynamicRequestBuilder(secrets),
        api,
        new JsonValidationEngine(),
        settings,
        new ConsoleProgressSink());

    var summary = await engine.ExecuteAsync(filter, CancellationToken.None);
    return summary.Failed > 0 || summary.PublicationFailures > 0 ? 1 : 0;

    string? Get(string name) => options.TryGetValue(name, out var value) ? value : null;
}
catch (Exception e)
{
    Console.Error.WriteLine($"Framework error: {e.Message}");
    return 2;
}

static void PrintHelp()
{
    Console.WriteLine("""
        Playwright API Automation Framework

        Usage:
          ApiAutomation --environment <name> [options]

        Required:
          --environment <name>     Environment key from appsettings.json (e.g. qa)

        Optional filters:
          --suite <name>           Limit to a suite
          --api <name>             Limit to an API name
          --scenario <name>        Limit to a scenario
          --test-case <id>         Run a single test-case ID

        Execution:
          --parallelism <n>        Concurrent test executions (default: 1). Fail-fast forces sequential.
          --fail-fast              Stop after the first Failed / Error / Invalid result
          --config <path>          Path to appsettings.json (default: beside the executable)

        Other:
          --help, -h               Show this help

        Secrets (environment variables):
          SHAREPOINT_ACCESS_TOKEN  Microsoft Graph bearer token (or the name configured in SharePoint.AccessTokenSecret)
          API_*_BEARER_TOKEN       Per-environment API tokens referenced by BearerTokenSecret

        Exit codes:
          0  All selected cases passed and results published
          1  Functional failures and/or publication failures
          2  Framework / configuration error
        """);
}

static Dictionary<string, string?> Parse(string[] values)
{
    var output = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < values.Length; i++)
    {
        if (!values[i].StartsWith("--", StringComparison.Ordinal) && values[i] is not ("-h"))
            throw new ArgumentException($"Unexpected argument '{values[i]}'.");

        var key = values[i].StartsWith("--", StringComparison.Ordinal) ? values[i][2..] : values[i].TrimStart('-');
        output[key] = i + 1 < values.Length && !values[i + 1].StartsWith('-') ? values[++i] : null;
    }
    return output;
}
