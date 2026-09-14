using System.Text.Json;
using ApiAutomation.Core.Interfaces;
using ApiAutomation.Core.Models;

namespace ApiAutomation.Core.Validation;

/// <summary>Evaluates status, header, and supported JSON-path rules without API-specific test classes.</summary>
public sealed class JsonValidationEngine : IValidationEngine
{
    /// <summary>Returns every validation outcome so one response can explain all observed failures.</summary>
    public IReadOnlyList<ValidationResult> Validate(TestCaseDefinition testCase, ApiResponse response)
    {
        var results = new List<ValidationResult> { Compare("status-code", response.StatusCode.ToString(), testCase.ExpectedStatusCode.ToString(), "equals") };
        JsonDocument? json = null;
        if (testCase.ValidationRules.Any(x => x.Type.StartsWith("json", StringComparison.OrdinalIgnoreCase)))
        {
            try { json = JsonDocument.Parse(response.Body); }
            catch (JsonException e) { results.Add(new("response-json", false, "valid JSON", null, e.Message)); return results; }
        }
        using (json)
            foreach (var rule in testCase.ValidationRules) results.Add(ValidateRule(rule, response, json));
        return results;
    }
    /// <summary>Dispatches a single data-defined rule to the appropriate response source.</summary>
    private static ValidationResult ValidateRule(ValidationRule rule, ApiResponse response, JsonDocument? json)
    {
        if (rule.Type.Equals("header", StringComparison.OrdinalIgnoreCase)) return Compare(rule.Id, response.Headers.TryGetValue(rule.Path ?? "", out var value) ? value : null, rule.ExpectedValue, rule.Operator ?? "equals");
        if (!rule.Type.StartsWith("json", StringComparison.OrdinalIgnoreCase)) return new(rule.Id, false, rule.ExpectedValue ?? "", null, $"Unsupported validation type '{rule.Type}'.");
        if (!TryGetJsonPath(json!.RootElement, rule.Path, out var element)) return new(rule.Id, false, rule.ExpectedValue ?? "exists", null, $"JSON path '{rule.Path}' was not found.");
        var actual = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        return Compare(rule.Id, actual, rule.ExpectedValue, rule.Operator ?? rule.Type[4..]);
    }
    /// <summary>Performs a normalized, case-insensitive comparison and records a diagnostic on failure.</summary>
    private static ValidationResult Compare(string id, string? actual, string? expected, string operation)
    {
        var passed = operation.ToLowerInvariant() switch { "exists" => actual is not null, "not-null" => !string.IsNullOrWhiteSpace(actual) && actual != "null", "contains" => actual?.Contains(expected ?? "", StringComparison.OrdinalIgnoreCase) == true, "greater-than" => decimal.TryParse(actual, out var a) && decimal.TryParse(expected, out var e) && a > e, "equals" or "" => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase), _ => false };
        return new(id, passed, expected ?? "", actual, passed ? null : $"Expected {operation} '{expected}', received '{actual}'.");
    }
    /// <summary>Traverses a dot-delimited object path; array indexing is intentionally not yet supported.</summary>
    private static bool TryGetJsonPath(JsonElement root, string? path, out JsonElement element)
    {
        element = root; if (string.IsNullOrWhiteSpace(path)) return true;
        foreach (var part in path.Trim().TrimStart('$', '.').Split('.', StringSplitOptions.RemoveEmptyEntries))
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(part, out element)) return false;
        return true;
    }
}
