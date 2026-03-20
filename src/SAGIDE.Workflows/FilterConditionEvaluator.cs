using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAGIDE.Workflows;

/// <summary>
/// Evaluates simple filter conditions of the form
/// <c>{field} {op} {value}</c> against JSON objects or plain text lines.
/// <para>
/// Supported operators: <c>&lt;=</c> <c>&gt;=</c> <c>&lt;</c> <c>&gt;</c> <c>==</c> <c>!=</c> <c>contains</c> <c>not_contains</c>
/// </para>
/// <example>
///   <c>pct_change &lt;= -5</c>  — numeric comparison on a JSON property<br/>
///   <c>status == "active"</c>  — string equality<br/>
///   <c>symbol contains "AA"</c> — substring check
/// </example>
/// </summary>
public static class FilterConditionEvaluator
{
    // Matches: fieldName op value   (value can be quoted string or number, possibly negative)
    private static readonly Regex ConditionRegex = new(
        @"^(?<field>\w+)\s+(?<op><=|>=|!=|==|<|>|contains|not_contains)\s+(?<value>"".+?""|-?\d+(?:\.\d+)?|\w+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Filters the <paramref name="input"/> string using the <paramref name="condition"/>.
    /// <list type="bullet">
    ///   <item>If input is a JSON array, each element is tested against the condition.</item>
    ///   <item>If input is a set of JSON objects separated by <c>\n---\n</c>, each section is tested.</item>
    ///   <item>Otherwise each non-empty line is tested as a plain string against the value side.</item>
    /// </list>
    /// </summary>
    public static string Filter(string input, string condition, int limit = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(input))   return string.Empty;
        if (string.IsNullOrWhiteSpace(condition)) return ApplyLimit(input, limit);

        var m = ConditionRegex.Match(condition.Trim());
        if (!m.Success)
            return ApplyLimit(input, limit); // unrecognized syntax → pass-through

        var field = m.Groups["field"].Value;
        var op    = m.Groups["op"].Value.ToLowerInvariant();
        var raw   = m.Groups["value"].Value;
        var compareValue = raw.Trim('"');

        // --- Try JSON array first ---
        if (TryParseJsonArray(input, out var elements))
        {
            var passing = elements
                .Where(el => EvalElement(el, field, op, compareValue))
                .Take(limit)
                .Select(el => el.GetRawText())
                .ToList();
            return string.Join("\n---\n", passing);
        }

        // --- Try section-delimited JSON objects (output of web_api_batch) ---
        var sections = input.Split("\n---\n", StringSplitOptions.RemoveEmptyEntries);
        if (sections.Length > 1)
        {
            var passing = sections
                .Where(sec =>
                {
                    if (TryParseJsonObject(sec.Trim(), out var el))
                        return EvalElement(el, field, op, compareValue);
                    return EvalText(sec, op, compareValue);
                })
                .Take(limit)
                .ToList();
            return string.Join("\n---\n", passing);
        }

        // --- Line-by-line text filter ---
        var lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var matched = lines
            .Where(line =>
            {
                if (TryParseJsonObject(line.Trim(), out var el))
                    return EvalElement(el, field, op, compareValue);
                return EvalText(line, op, compareValue);
            })
            .Take(limit)
            .ToList();
        return string.Join('\n', matched);
    }

    // ── Evaluation helpers ────────────────────────────────────────────────────

    private static bool EvalElement(JsonElement el, string field, string op, string compareValue)
    {
        if (!el.TryGetProperty(field, out var prop) &&
            !el.TryGetProperty(ToSnake(field), out prop))
            return false;

        // Try numeric comparison
        if (double.TryParse(compareValue, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var numVal))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var propNum))
                return CompareNumeric(propNum, op, numVal);
        }

        // String comparison / contains
        var propStr = prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? string.Empty
            : prop.GetRawText();

        return EvalText(propStr, op, compareValue);
    }

    private static bool EvalText(string text, string op, string compareValue) => op switch
    {
        "contains"     => text.Contains(compareValue, StringComparison.OrdinalIgnoreCase),
        "not_contains" => !text.Contains(compareValue, StringComparison.OrdinalIgnoreCase),
        "==" => string.Equals(text.Trim(), compareValue, StringComparison.OrdinalIgnoreCase),
        "!=" => !string.Equals(text.Trim(), compareValue, StringComparison.OrdinalIgnoreCase),
        _    => false  // <, >, <=, >= not meaningful for non-numeric strings
    };

    private static bool CompareNumeric(double actual, string op, double threshold) => op switch
    {
        "<="  => actual <= threshold,
        ">="  => actual >= threshold,
        "<"   => actual <  threshold,
        ">"   => actual >  threshold,
        "=="  => Math.Abs(actual - threshold) < 1e-10,
        "!="  => Math.Abs(actual - threshold) >= 1e-10,
        _     => false,
    };

    // ── Parse helpers ─────────────────────────────────────────────────────────

    private static bool TryParseJsonArray(string text, out List<JsonElement> elements)
    {
        elements = [];
        try
        {
            var trimmed = text.Trim();
            if (!trimmed.StartsWith('[')) return false;
            var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
            elements = [..doc.RootElement.EnumerateArray()];
            return true;
        }
        catch { return false; }
    }

    private static bool TryParseJsonObject(string text, out JsonElement element)
    {
        element = default;
        try
        {
            var trimmed = text.Trim();
            if (!trimmed.StartsWith('{')) return false;
            var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            element = doc.RootElement;
            return true;
        }
        catch { return false; }
    }

    private static string ApplyLimit(string input, int limit)
    {
        if (limit == int.MaxValue) return input;
        var sections = input.Split("\n---\n", StringSplitOptions.RemoveEmptyEntries);
        if (sections.Length > 1)
            return string.Join("\n---\n", sections.Take(limit));
        var lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', lines.Take(limit));
    }

    /// <summary>Converts camelCase/PascalCase to snake_case for property lookup fallback.</summary>
    private static string ToSnake(string s) =>
        Regex.Replace(s, "(?<=[a-z])([A-Z])", "_$1").ToLowerInvariant();
}
