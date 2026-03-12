using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VirtualLab.Application.ContractValidation;
using VirtualLab.Domain.Profiles;

namespace VirtualLab.Infrastructure.ContractValidation;

public sealed class ContractValidationService : IContractValidationService
{
    private static readonly Regex PathTokenRegex = new(@"([^\.\[\]]+)|\[(\d+)\]", RegexOptions.Compiled);

    public PayloadContractValidationReport Validate(
        FccProfileContract contract,
        string scope,
        string rawPayloadJson,
        string canonicalPayloadJson)
    {
        string normalizedScope = Normalize(scope);
        List<FccValidationRuleDefinition> rules = contract.ValidationRules
            .Where(rule => AppliesToScope(rule.Scope, normalizedScope))
            .ToList();
        List<FccFieldMappingDefinition> mappings = contract.FieldMappings
            .Where(mapping =>
                string.IsNullOrWhiteSpace(mapping.Direction) ||
                string.Equals(mapping.Direction, "Inbound", StringComparison.OrdinalIgnoreCase))
            .Where(mapping => AppliesToScope(mapping.Scope, normalizedScope))
            .ToList();

        if (rules.Count == 0 && mappings.Count == 0)
        {
            return PayloadContractValidationReport.Disabled(normalizedScope);
        }

        List<PayloadContractValidationIssue> issues = [];
        List<PayloadFieldComparison> comparisons = [];

        JsonElement rawRoot = ParsePayload(rawPayloadJson, "raw", issues);
        JsonElement canonicalRoot = ParsePayload(canonicalPayloadJson, "canonical", issues);

        foreach (FccValidationRuleDefinition rule in rules)
        {
            EvaluateRule(rule, normalizedScope, rawRoot, canonicalRoot, issues);
        }

        foreach (FccFieldMappingDefinition mapping in mappings)
        {
            comparisons.Add(EvaluateMapping(mapping, normalizedScope, rawRoot, canonicalRoot));
        }

        int errorCount = issues.Count(issue => string.Equals(issue.Severity, "Error", StringComparison.OrdinalIgnoreCase));
        int warningCount = issues.Count(issue => string.Equals(issue.Severity, "Warning", StringComparison.OrdinalIgnoreCase)) +
            comparisons.Count(comparison => comparison.Status is "MissingRaw" or "MissingCanonical" or "Mismatch");
        int matchedCount = comparisons.Count(comparison => comparison.Status == "Matched");
        int missingCount = comparisons.Count(comparison => comparison.Status is "MissingRaw" or "MissingCanonical");
        int mismatchCount = comparisons.Count(comparison => comparison.Status == "Mismatch");

        return new PayloadContractValidationReport(
            normalizedScope,
            true,
            ResolveOutcome(errorCount, warningCount),
            errorCount,
            warningCount,
            matchedCount,
            missingCount,
            mismatchCount,
            issues,
            comparisons);
    }

    private static void EvaluateRule(
        FccValidationRuleDefinition rule,
        string requestedScope,
        JsonElement rawRoot,
        JsonElement canonicalRoot,
        ICollection<PayloadContractValidationIssue> issues)
    {
        string payloadKind = ResolvePayloadKind(rule.Scope, requestedScope);
        JsonElement root = payloadKind == "raw" ? rawRoot : canonicalRoot;
        string path = string.IsNullOrWhiteSpace(rule.Expression) ? "$" : rule.Expression.Trim();

        if (!TryResolvePath(root, path, out JsonElement value))
        {
            issues.Add(new PayloadContractValidationIssue(
                string.IsNullOrWhiteSpace(rule.RuleKey) ? "field_missing" : rule.RuleKey,
                rule.Required ? "Error" : "Warning",
                payloadKind,
                path,
                string.IsNullOrWhiteSpace(rule.Message) ? $"Required field '{path}' was not found." : rule.Message));
            return;
        }

        if (IsEmpty(value))
        {
            issues.Add(new PayloadContractValidationIssue(
                string.IsNullOrWhiteSpace(rule.RuleKey) ? "field_empty" : rule.RuleKey,
                rule.Required ? "Error" : "Warning",
                payloadKind,
                path,
                string.IsNullOrWhiteSpace(rule.Message) ? $"Required field '{path}' was empty." : rule.Message));
            return;
        }

        string? invalidMessage = ValidateRuleValue(rule, value, path);
        if (invalidMessage is not null)
        {
            issues.Add(new PayloadContractValidationIssue(
                string.IsNullOrWhiteSpace(rule.RuleKey) ? "field_invalid" : rule.RuleKey,
                rule.Required ? "Error" : "Warning",
                payloadKind,
                path,
                invalidMessage));
        }
    }

    private static PayloadFieldComparison EvaluateMapping(
        FccFieldMappingDefinition mapping,
        string requestedScope,
        JsonElement rawRoot,
        JsonElement canonicalRoot)
    {
        bool hasRaw = TryResolvePath(rawRoot, mapping.SourceField, out JsonElement rawValue);
        bool hasCanonical = TryResolvePath(canonicalRoot, mapping.TargetField, out JsonElement canonicalValue);

        hasRaw = hasRaw && !IsEmpty(rawValue);
        hasCanonical = hasCanonical && !IsEmpty(canonicalValue);

        if (!hasRaw && !hasCanonical)
        {
            return new PayloadFieldComparison(
                requestedScope,
                mapping.SourceField,
                mapping.TargetField,
                "NotPresent",
                null,
                null,
                mapping.Transform,
                "Neither payload currently exposes this mapped field.");
        }

        if (!hasRaw)
        {
            return new PayloadFieldComparison(
                requestedScope,
                mapping.SourceField,
                mapping.TargetField,
                "MissingRaw",
                null,
                ToComparableString(canonicalValue),
                mapping.Transform,
                "Canonical payload has a mapped value but the raw source field was missing.");
        }

        if (!hasCanonical)
        {
            return new PayloadFieldComparison(
                requestedScope,
                mapping.SourceField,
                mapping.TargetField,
                "MissingCanonical",
                ToComparableString(rawValue),
                null,
                mapping.Transform,
                "Raw payload has a mapped value but the canonical target field was missing.");
        }

        string transformedRaw = ApplyTransform(rawValue, mapping.Transform);
        string canonicalText = ToComparableString(canonicalValue);
        bool matched = string.Equals(transformedRaw, canonicalText, StringComparison.OrdinalIgnoreCase);

        return new PayloadFieldComparison(
            requestedScope,
            mapping.SourceField,
            mapping.TargetField,
            matched ? "Matched" : "Mismatch",
            transformedRaw,
            canonicalText,
            mapping.Transform,
            matched
                ? "Raw and canonical payloads agree for this mapping."
                : "Raw and canonical payloads disagree for this mapping.");
    }

    private static JsonElement ParsePayload(
        string payloadJson,
        string payloadKind,
        ICollection<PayloadContractValidationIssue> issues)
    {
        string candidate = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson;

        try
        {
            using JsonDocument document = JsonDocument.Parse(candidate);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            issues.Add(new PayloadContractValidationIssue(
                $"{payloadKind}_json_invalid",
                "Error",
                payloadKind,
                "$",
                $"The {payloadKind} payload could not be parsed as JSON."));

            using JsonDocument document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }
    }

    private static string? ValidateRuleValue(FccValidationRuleDefinition rule, JsonElement value, string path)
    {
        string expectedType = Normalize(rule.ExpectedType);
        if (!string.IsNullOrWhiteSpace(expectedType) && !MatchesExpectedType(value, expectedType))
        {
            return string.IsNullOrWhiteSpace(rule.Message)
                ? $"Field '{path}' did not match expected type '{rule.ExpectedType}'."
                : rule.Message;
        }

        if (rule.Minimum.HasValue || rule.Maximum.HasValue)
        {
            if (!TryGetDecimal(value, out decimal numericValue))
            {
                return string.IsNullOrWhiteSpace(rule.Message)
                    ? $"Field '{path}' must be numeric."
                    : rule.Message;
            }

            if (rule.Minimum.HasValue && numericValue < rule.Minimum.Value)
            {
                return string.IsNullOrWhiteSpace(rule.Message)
                    ? $"Field '{path}' must be at least {rule.Minimum.Value.ToString(CultureInfo.InvariantCulture)}."
                    : rule.Message;
            }

            if (rule.Maximum.HasValue && numericValue > rule.Maximum.Value)
            {
                return string.IsNullOrWhiteSpace(rule.Message)
                    ? $"Field '{path}' must be at most {rule.Maximum.Value.ToString(CultureInfo.InvariantCulture)}."
                    : rule.Message;
            }
        }

        if (!string.IsNullOrWhiteSpace(rule.Pattern))
        {
            string text = ToComparableString(value);
            if (!Regex.IsMatch(text, rule.Pattern))
            {
                return string.IsNullOrWhiteSpace(rule.Message)
                    ? $"Field '{path}' did not match pattern '{rule.Pattern}'."
                    : rule.Message;
            }
        }

        if (rule.AllowedValues.Count > 0)
        {
            string text = ToComparableString(value);
            if (!rule.AllowedValues.Any(item => string.Equals(item, text, StringComparison.OrdinalIgnoreCase)))
            {
                return string.IsNullOrWhiteSpace(rule.Message)
                    ? $"Field '{path}' must be one of: {string.Join(", ", rule.AllowedValues)}."
                    : rule.Message;
            }
        }

        return null;
    }

    private static bool MatchesExpectedType(JsonElement value, string expectedType)
    {
        return expectedType switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "date-time" => value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            "uuid" => value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out _),
            _ => true,
        };
    }

    private static bool TryResolvePath(JsonElement root, string path, out JsonElement value)
    {
        string normalizedPath = NormalizePath(path);
        value = root;

        if (normalizedPath == "$")
        {
            return true;
        }

        JsonElement current = root;
        foreach (Match match in PathTokenRegex.Matches(normalizedPath))
        {
            if (match.Groups[1].Success)
            {
                if (current.ValueKind != JsonValueKind.Object ||
                    !TryGetProperty(current, match.Groups[1].Value, out current))
                {
                    value = default;
                    return false;
                }

                continue;
            }

            if (!match.Groups[2].Success ||
                current.ValueKind != JsonValueKind.Array ||
                !TryGetArrayElement(current, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), out current))
            {
                value = default;
                return false;
            }
        }

        value = current;
        return true;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetArrayElement(JsonElement element, int index, out JsonElement value)
    {
        int currentIndex = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (currentIndex == index)
            {
                value = item;
                return true;
            }

            currentIndex++;
        }

        value = default;
        return false;
    }

    private static bool TryGetDecimal(JsonElement value, out decimal numericValue)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out numericValue))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out numericValue))
        {
            return true;
        }

        numericValue = 0;
        return false;
    }

    private static bool IsEmpty(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => true,
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Array => !value.EnumerateArray().Any(),
            JsonValueKind.Object => !value.EnumerateObject().Any(),
            _ => false,
        };
    }

    private static string ApplyTransform(JsonElement value, string transform)
    {
        string normalizedTransform = Normalize(transform);
        return normalizedTransform switch
        {
            "" or "identity" => ToComparableString(value),
            "major-to-minor" or "major-to-minor-units" => TransformNumber(value, 100m),
            "litres-to-microlitres" => TransformNumber(value, 1000000m),
            "uppercase" => ToComparableString(value).ToUpperInvariant(),
            "lowercase" => ToComparableString(value).ToLowerInvariant(),
            _ => ToComparableString(value),
        };
    }

    private static string TransformNumber(JsonElement value, decimal multiplier)
    {
        if (!TryGetDecimal(value, out decimal numericValue))
        {
            return ToComparableString(value);
        }

        long result = decimal.ToInt64(decimal.Round(numericValue * multiplier, 0, MidpointRounding.AwayFromZero));
        return result.ToString(CultureInfo.InvariantCulture);
    }

    private static string ToComparableString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number when value.TryGetInt64(out long integer) => integer.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.Number when value.TryGetDecimal(out decimal number) => number.ToString("0.#############################", CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => value.GetRawText(),
        };
    }

    private static string ResolvePayloadKind(string ruleScope, string requestedScope)
    {
        string normalizedRuleScope = Normalize(ruleScope);
        if (normalizedRuleScope.EndsWith(".raw", StringComparison.Ordinal))
        {
            return "raw";
        }

        if (normalizedRuleScope.EndsWith(".canonical", StringComparison.Ordinal))
        {
            return "canonical";
        }

        return requestedScope == ContractValidationScopes.PreAuthRequest ? "canonical" : "canonical";
    }

    private static bool AppliesToScope(string configuredScope, string requestedScope)
    {
        string normalizedConfiguredScope = Normalize(configuredScope);
        if (string.IsNullOrWhiteSpace(normalizedConfiguredScope))
        {
            return requestedScope == ContractValidationScopes.Transaction;
        }

        return requestedScope switch
        {
            ContractValidationScopes.Transaction => normalizedConfiguredScope == ContractValidationScopes.Transaction ||
                normalizedConfiguredScope.StartsWith($"{ContractValidationScopes.Transaction}.", StringComparison.Ordinal),
            ContractValidationScopes.PreAuthRequest => normalizedConfiguredScope == "request" ||
                normalizedConfiguredScope == ContractValidationScopes.PreAuthRequest ||
                normalizedConfiguredScope.StartsWith($"{ContractValidationScopes.PreAuthRequest}.", StringComparison.Ordinal),
            ContractValidationScopes.PreAuthResponse => normalizedConfiguredScope == ContractValidationScopes.PreAuthResponse ||
                normalizedConfiguredScope.StartsWith($"{ContractValidationScopes.PreAuthResponse}.", StringComparison.Ordinal),
            _ => normalizedConfiguredScope == requestedScope ||
                normalizedConfiguredScope.StartsWith($"{requestedScope}.", StringComparison.Ordinal),
        };
    }

    private static string ResolveOutcome(int errorCount, int warningCount)
    {
        if (errorCount > 0)
        {
            return "Failed";
        }

        return warningCount > 0 ? "Warning" : "Passed";
    }

    private static string NormalizePath(string path)
    {
        string normalized = string.IsNullOrWhiteSpace(path) ? "$" : path.Trim();
        if (normalized == "$")
        {
            return normalized;
        }

        if (normalized.StartsWith("$.", StringComparison.Ordinal))
        {
            return normalized[2..];
        }

        if (normalized.StartsWith("$", StringComparison.Ordinal))
        {
            return normalized[1..];
        }

        return normalized;
    }

    private static string Normalize(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
}
