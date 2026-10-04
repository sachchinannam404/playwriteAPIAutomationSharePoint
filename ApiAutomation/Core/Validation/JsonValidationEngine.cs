using System.Text.Json;
using System.Text.RegularExpressions;
using ApiAutomation.Core.Interfaces;
using ApiAutomation.Core.Models;

namespace ApiAutomation.Core.Validation;

/// <summary>Evaluates status, header, and JSON-path rules without API-specific test classes.</summary>
public sealed class JsonValidationEngine : IValidationEngine
{
    private static readonly Regex ArrayIndex = new(@"^(\w+)\[(\d+)\]$", RegexOptions.Compiled);

    public IReadOnlyList<ValidationResult> Validate(TestCaseDefinition testCase, ApiResponse response)
    {
        var results = new List<ValidationResult>
        {
            Compare("status-code", response.StatusCode.ToString(), testCase.ExpectedStatusCode.ToString(), "equals")
        };

        JsonDocument? json = null;
        if (testCase.ValidationRules.Any(x => x.Type.StartsWith("json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                json = JsonDocument.Parse(response.Body);
            }
            catch (JsonException e)
            {
                results.Add(new("response-json", false, "valid JSON", null, e.Message));
                return results;
            }
        }

        using (json)
        {
            foreach (var rule in testCase.ValidationRules)
                results.Add(ValidateRule(rule, response, json));
        }

        return results;
    }

    private static ValidationResult ValidateRule(ValidationRule rule, ApiResponse response, JsonDocument? json)
    {
        if (rule.Type.Equals("header", StringComparison.OrdinalIgnoreCase))
        {
            var actual = FindHeader(response.Headers, rule.Path);
            return Compare(rule.Id, actual, rule.ExpectedValue, rule.Operator ?? "equals");
        }

        if (!rule.Type.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            return new(rule.Id, false, rule.ExpectedValue ?? "", null, $"Unsupported validation type '{rule.Type}'.");

        if (!TryGetJsonPath(json!.RootElement, rule.Path, out var element))
            return new(rule.Id, false, rule.ExpectedValue ?? "exists", null, $"JSON path '{rule.Path}' was not found.");

        var actualValue = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        return Compare(rule.Id, actualValue, rule.ExpectedValue, rule.Operator ?? rule.Type[4..]);
    }

    /// <summary>Case-insensitive header lookup (Playwright and most HTTP stacks lower-case header names).</summary>
    private static string? FindHeader(IReadOnlyDictionary<string, string> headers, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (headers.TryGetValue(name, out var exact)) return exact;
        foreach (var kv in headers)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }
        return null;
    }

    private static ValidationResult Compare(string id, string? actual, string? expected, string operation)
    {
        var op = operation.ToLowerInvariant();
        var passed = op switch
        {
            "exists" => actual is not null,
            "not-null" => !string.IsNullOrWhiteSpace(actual) && actual != "null",
            "contains" => actual?.Contains(expected ?? "", StringComparison.OrdinalIgnoreCase) == true,
            "starts-with" or "startswith" => actual?.StartsWith(expected ?? "", StringComparison.OrdinalIgnoreCase) == true,
            "ends-with" or "endswith" => actual?.EndsWith(expected ?? "", StringComparison.OrdinalIgnoreCase) == true,
            "greater-than" or "gt" => decimal.TryParse(actual, out var aGt) && decimal.TryParse(expected, out var eGt) && aGt > eGt,
            "less-than" or "lt" => decimal.TryParse(actual, out var aLt) && decimal.TryParse(expected, out var eLt) && aLt < eLt,
            "not-equals" or "ne" or "not-equal" => !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            "regex" or "matches" => actual is not null && expected is not null &&
                                     Regex.IsMatch(actual, expected, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "equals" or "" or "eq" => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            _ => false
        };

        return new(id, passed, expected ?? "", actual,
            passed ? null : $"Expected {operation} '{expected}', received '{actual}'.");
    }

    private static bool TryGetJsonPath(JsonElement root, string? path, out JsonElement element)
    {
        element = root;
        if (string.IsNullOrWhiteSpace(path)) return true;

        var normalized = path.Trim().TrimStart('$', '.');
        foreach (var part in normalized.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = ArrayIndex.Match(part);
            if (match.Success)
            {
                var prop = match.Groups[1].Value;
                var index = int.Parse(match.Groups[2].Value);
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(prop, out element))
                    return false;
                if (element.ValueKind != JsonValueKind.Array || index < 0 || index >= element.GetArrayLength())
                    return false;
                element = element[index];
            }
            else if (int.TryParse(part, out var bareIndex))
            {
                if (element.ValueKind != JsonValueKind.Array || bareIndex < 0 || bareIndex >= element.GetArrayLength())
                    return false;
                element = element[bareIndex];
            }
            else
            {
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(part, out element))
                    return false;
            }
        }

        return true;
    }
}
