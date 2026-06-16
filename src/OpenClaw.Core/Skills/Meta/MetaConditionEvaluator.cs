using System.Text.RegularExpressions;

namespace OpenClaw.Core.Skills;

public sealed class MetaConditionEvaluator
{
    // Matches a leading "not " at the top level (outside parens/strings).
    private static readonly Regex NotPrefix = new(
        @"^not\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly MetaTemplateRenderer _renderer;

    public MetaConditionEvaluator(MetaTemplateRenderer renderer)
    {
        _renderer = renderer;
    }

    public bool Evaluate(string? expression, MetaExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var candidate = expression.Trim();
        var parts = SplitByTopLevelOperators(candidate);

        if (parts.Count == 1)
            return EvaluateAtomic(parts[0].Expression, context);

        // ── combine with correct precedence ──
        // Evaluate all atomic expressions first, then combine respecting
        // "and" > "or" precedence: group by "or" boundaries, each group
        // is evaluated as a chain of "and".
        var evaluated = new List<bool>(parts.Count);
        foreach (var part in parts)
            evaluated.Add(EvaluateAtomic(part.Expression, context));

        var orResults = new List<bool>();
        var currentAnd = evaluated[0];

        for (var i = 1; i < parts.Count; i++)
        {
            var op = parts[i - 1].Operator;
            if (string.Equals(op, "and", StringComparison.OrdinalIgnoreCase))
            {
                currentAnd = currentAnd && evaluated[i];
            }
            else // "or"
            {
                orResults.Add(currentAnd);
                currentAnd = evaluated[i];
            }
        }
        orResults.Add(currentAnd);

        // Any "or" group being true makes the whole expression true.
        foreach (var r in orResults)
            if (r) return true;

        return false;
    }

    /// <summary>
    /// Evaluates a single atomic expression (no top-level logical operators) by
    /// rendering it through Jinja2.NET and checking truthiness.
    /// Supports a leading "not" prefix for negation.
    ///
    /// When the expression is already wrapped in {{ ... }} (Jinja2 variable
    /// syntax) AND contains "and"/"or" inside the delimiters, it recursively
    /// unwraps, splits, and re-evaluates — because Jinja2.NET's renderer does
    /// not support the "and"/"or" binary operators at the AST level.
    /// </summary>
    private bool EvaluateAtomic(string expression, MetaExecutionContext context)
    {
        var candidate = expression.Trim();

        // ── pre-wrapped {{ … and … }} fallback ──
        // When the expression is already wrapped in {{ }} or {% %} AND
        // contains logical operators inside those delimiters, Jinja2.NET
        // will fail at render time.  Unwrap the inner expression and
        // recursively evaluate via the full Evaluate path.
        if ((candidate.StartsWith("{{", StringComparison.Ordinal)
                || candidate.StartsWith("{%", StringComparison.Ordinal))
            && (candidate.Contains(" and ", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains(" or ", StringComparison.OrdinalIgnoreCase)))
        {
            // Extract content between delimiters.
            var inner = candidate;
            if (candidate.StartsWith("{{", StringComparison.Ordinal)
                && candidate.EndsWith("}}", StringComparison.Ordinal))
            {
                inner = candidate[2..^2].Trim();
            }
            else if (candidate.StartsWith("{%", StringComparison.Ordinal)
                     && candidate.EndsWith("%}", StringComparison.Ordinal))
            {
                // {% if cond %}true{% endif %} — extract only the condition,
                // which is the expression after "if"/"unless".
                inner = ExtractIfCondition(inner);
            }

            return Evaluate(inner, context);
        }

        // ── normal atomic evaluation ──
        // Handle "not" prefix at the top level.
        var negate = false;
        var match = NotPrefix.Match(candidate);
        if (match.Success)
        {
            negate = true;
            candidate = candidate[match.Length..].Trim();
        }

        var template = candidate.Contains("{{", StringComparison.Ordinal)
            || candidate.Contains("{%", StringComparison.Ordinal)
            ? candidate
            : "{{ " + candidate + " }}";

        var rendered = _renderer.Render(template, context);
        var result = IsTruthy(rendered);

        return negate ? !result : result;
    }

    /// <summary>
    /// Extracts the condition expression from a {% if ... %} or {% unless ... %} block.
    /// Handles full form: {% if CONDITION %}body{% endif %}
    /// </summary>
    private static string ExtractIfCondition(string template)
    {
        var inner = template[2..]; // strip leading {%
        var endIdx = inner.IndexOf("%}", StringComparison.Ordinal);
        if (endIdx < 0)
            return inner;
        inner = inner[..endIdx].Trim();

        // Strip the "if" or "unless" keyword.
        const string ifKeyword = "if ";
        const string unlessKeyword = "unless ";
        if (inner.StartsWith(ifKeyword, StringComparison.OrdinalIgnoreCase))
            return inner[ifKeyword.Length..].Trim();
        if (inner.StartsWith(unlessKeyword, StringComparison.OrdinalIgnoreCase))
            return inner[unlessKeyword.Length..].Trim();

        return inner;
    }

    /// <summary>
    /// Splits an expression at top-level "and"/"or" boundaries, respecting
    /// string literals, parentheses grouping, and nested {{ ... }} / {% ... %}
    /// delimiters.
    /// </summary>
    internal static List<(string Expression, string Operator)> SplitByTopLevelOperators(string expression)
    {
        var parts = new List<(string Expression, string Operator)>();
        var depth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inCurly = 0; // depth of {{ }} / {% %} nesting
        var lastSplit = 0;

        for (var i = 0; i < expression.Length; i++)
        {
            var ch = expression[i];

            // Track string literal boundaries.
            if (ch == '\'' && !inDoubleQuote)
                inSingleQuote = !inSingleQuote;
            else if (ch == '"' && !inSingleQuote)
                inDoubleQuote = !inDoubleQuote;

            if (inSingleQuote || inDoubleQuote)
                continue;

            // Track parentheses depth.
            if (ch == '(')
                depth++;
            else if (ch == ')')
                depth--;

            // Track {{ ... }} / {% ... %} depth — no operators inside.
            if (ch == '{' && i + 1 < expression.Length && expression[i + 1] is '{' or '%')
            {
                inCurly++;
                i++; // skip second brace
                continue;
            }
            if (ch is '}' or '%' && i + 1 < expression.Length && expression[i + 1] == '}')
            {
                if (inCurly > 0)
                    inCurly--;
                i++;
                continue;
            }

            // Only split at depth 0 and outside Jinja delimiters.
            if (depth > 0 || inCurly > 0)
                continue;

            // Check for "and" / "or" at word boundaries.
            if (TryMatchLogicalOperator(expression, i, "and", out var nextIndex))
            {
                var expr = expression[lastSplit..i].Trim();
                if (expr.Length > 0)
                    parts.Add((expr, "and"));
                i = nextIndex - 1;
                lastSplit = nextIndex;
                continue;
            }

            if (TryMatchLogicalOperator(expression, i, "or", out nextIndex))
            {
                var expr = expression[lastSplit..i].Trim();
                if (expr.Length > 0)
                    parts.Add((expr, "or"));
                i = nextIndex - 1;
                lastSplit = nextIndex;
                continue;
            }
        }

        // Add the trailing part.
        var tail = expression[lastSplit..].Trim();
        if (tail.Length > 0)
            parts.Add((tail, string.Empty));

        return parts;
    }

    private static bool TryMatchLogicalOperator(string expression, int index, string operatorText, out int nextIndex)
    {
        nextIndex = index;

        if (index < 0 || index + operatorText.Length > expression.Length)
            return false;

        if (!expression.AsSpan(index, operatorText.Length).Equals(operatorText, StringComparison.OrdinalIgnoreCase))
            return false;

        var beforeOk = index == 0 || char.IsWhiteSpace(expression[index - 1]) || expression[index - 1] == '(';
        if (!beforeOk)
            return false;

        var afterIndex = index + operatorText.Length;
        var afterOk = afterIndex == expression.Length || char.IsWhiteSpace(expression[afterIndex]) || expression[afterIndex] == ')';
        if (!afterOk)
            return false;

        nextIndex = afterIndex;
        while (nextIndex < expression.Length && char.IsWhiteSpace(expression[nextIndex]))
            nextIndex++;

        return true;
    }

    internal static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();
        if (bool.TryParse(normalized, out var boolValue))
            return boolValue;

        return !normalized.Equals("0", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("no", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("off", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("null", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("none", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("undefined", StringComparison.OrdinalIgnoreCase);
    }
}
