using Xunit;
using ApiAutomation.Core.Models;
using ApiAutomation.Core.Validation;
using ApiAutomation.Playwright;
using ApiAutomation.Core.Interfaces;

namespace ApiAutomation.Tests;

public sealed class ValidationAndRequestTests
{
    [Fact]
    public void Validates_nested_json_and_reports_the_failing_rule()
    {
        var test = TestCase(rules: [new("customer-status", "json", "$.customer.status", "Active", "equals")]);
        var response = new ApiResponse(200, new Dictionary<string, string>(), "{\"customer\":{\"status\":\"Inactive\"}}");

        var result = new JsonValidationEngine().Validate(test, response);

        Assert.Equal(2, result.Count);
        Assert.False(result.Single(x => x.RuleId == "customer-status").Passed);
        Assert.Contains("Active", result.Single(x => x.RuleId == "customer-status").FailureMessage!);
    }

    [Fact]
    public void Validates_array_index_json_path()
    {
        var test = TestCase(rules: [new("first-id", "json", "items[0].id", "42", "equals")]);
        var response = new ApiResponse(200, new Dictionary<string, string>(), "{\"items\":[{\"id\":42},{\"id\":99}]}");

        var result = new JsonValidationEngine().Validate(test, response);

        Assert.True(result.Single(x => x.RuleId == "first-id").Passed);
    }

    [Fact]
    public void Validates_numeric_array_segment_path()
    {
        var test = TestCase(rules: [new("second-name", "json", "items.1.name", "bob", "equals")]);
        var response = new ApiResponse(200, new Dictionary<string, string>(), "{\"items\":[{\"name\":\"alice\"},{\"name\":\"bob\"}]}");

        var result = new JsonValidationEngine().Validate(test, response);

        Assert.True(result.Single(x => x.RuleId == "second-name").Passed);
    }

    [Fact]
    public void Rejects_malformed_payload_and_unsupported_rule_before_execution()
    {
        var test = TestCase(payload: "{broken", rules: [new("unknown", "xml", null, null)]);

        var errors = TestDefinitionValidator.Validate(test);

        Assert.Contains(errors, x => x.Contains("Payload is not valid JSON", StringComparison.Ordinal));
        Assert.Contains(errors, x => x.Contains("unsupported type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Builds_encoded_url_and_injects_correlation_and_bearer_headers()
    {
        var test = TestCase(
            endpoint: "/customers/{id}",
            path: new Dictionary<string, string> { ["id"] = "a/b" },
            query: new Dictionary<string, string> { ["search"] = "Mary Jane" });
        var environment = new EnvironmentConfiguration("qa", "https://api.example.test", 1000, "TOKEN", new Dictionary<string, string> { ["Accept"] = "application/json" });
        var request = new DynamicRequestBuilder(new DictionarySecretResolver("TOKEN", "abc")).Build(test, environment, "run:test:1");

        Assert.Equal("https://api.example.test/customers/a%2Fb?search=Mary%20Jane", request.Url.AbsoluteUri);
        Assert.Equal("Bearer abc", request.Headers["Authorization"]);
        Assert.Equal("run:test:1", request.Headers["X-Correlation-ID"]);
    }

    [Fact]
    public void Builds_absolute_endpoint_without_base_url_join()
    {
        var test = TestCase(endpoint: "https://other.example.test/v1/health");
        var environment = new EnvironmentConfiguration("qa", "https://api.example.test", 1000, null, new Dictionary<string, string>());
        var request = new DynamicRequestBuilder(new DictionarySecretResolver("", "")).Build(test, environment, "corr");

        Assert.Equal("https://other.example.test/v1/health", request.Url.AbsoluteUri);
    }

    [Fact]
    public void Resolves_secret_placeholders_in_headers_and_payload()
    {
        var test = TestCase(
            endpoint: "/secure",
            payload: "{\"key\":\"{{secret:API_KEY}}\"}",
            headers: new Dictionary<string, string> { ["X-Api-Key"] = "{{secret:API_KEY}}" });
        var environment = new EnvironmentConfiguration("qa", "https://api.example.test", 1000, null, new Dictionary<string, string>());
        var request = new DynamicRequestBuilder(new DictionarySecretResolver("API_KEY", "s3cret")).Build(test, environment, "corr");

        Assert.Equal("s3cret", request.Headers["X-Api-Key"]);
        Assert.Equal("{\"key\":\"s3cret\"}", request.Payload);
    }

    private static TestCaseDefinition TestCase(
        string endpoint = "/health",
        string? payload = null,
        IReadOnlyList<ValidationRule>? rules = null,
        IReadOnlyDictionary<string, string>? path = null,
        IReadOnlyDictionary<string, string>? query = null,
        IReadOnlyDictionary<string, string>? headers = null) =>
        new("TC-1", "Smoke", "API", endpoint, "GET", "health", true, 1, 200,
            headers ?? new Dictionary<string, string>(),
            query ?? new Dictionary<string, string>(),
            path ?? new Dictionary<string, string>(),
            payload,
            rules ?? []);

    private sealed class DictionarySecretResolver(string name, string value) : ISecretResolver
    {
        public string? Resolve(string requested) => requested == name ? value : null;
    }
}
