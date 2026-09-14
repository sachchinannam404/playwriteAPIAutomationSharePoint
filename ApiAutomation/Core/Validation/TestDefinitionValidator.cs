using System.Text.Json;
using ApiAutomation.Core.Models;

namespace ApiAutomation.Core.Validation;

public static class TestDefinitionValidator
{
    private static readonly HashSet<string> Methods = new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "PATCH", "DELETE" };
    private static readonly HashSet<string> Operations = new(StringComparer.OrdinalIgnoreCase) { "equals", "contains", "exists", "not-null", "greater-than" };

    public static IReadOnlyList<string> Validate(TestCaseDefinition test)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(test.Id)) errors.Add("Test Case ID is required.");
        if (string.IsNullOrWhiteSpace(test.Endpoint) || !Uri.TryCreate(test.Endpoint, UriKind.RelativeOrAbsolute, out _)) errors.Add("Endpoint must be a valid relative or absolute URI.");
        if (!Methods.Contains(test.Method)) errors.Add($"Unsupported HTTP method '{test.Method}'.");
        if (test.ExpectedStatusCode is < 100 or > 599) errors.Add("Expected status code must be 100–599.");
        if (!string.IsNullOrWhiteSpace(test.Payload))
            try { JsonDocument.Parse(test.Payload); } catch (JsonException e) { errors.Add($"Payload is not valid JSON: {e.Message}"); }
        foreach (var rule in test.ValidationRules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id)) errors.Add("Every validation rule requires an ID.");
            if (!rule.Type.Equals("header", StringComparison.OrdinalIgnoreCase) && !rule.Type.StartsWith("json", StringComparison.OrdinalIgnoreCase)) errors.Add($"Validation rule '{rule.Id}' has unsupported type '{rule.Type}'.");
            var operation = rule.Operator ?? (rule.Type.StartsWith("json", StringComparison.OrdinalIgnoreCase) ? rule.Type[4..] : "equals");
            if (!Operations.Contains(operation)) errors.Add($"Validation rule '{rule.Id}' has unsupported operator '{operation}'.");
        }
        return errors;
    }
}
