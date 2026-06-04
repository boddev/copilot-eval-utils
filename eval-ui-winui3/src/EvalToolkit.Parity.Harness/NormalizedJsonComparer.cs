using System.Text.Encodings.Web;
using System.Text.Json;

namespace EvalToolkit.Parity.Harness;

/// <summary>
/// Structural JSON comparison configured for parity-diffing the TS
/// envelope against a C# envelope. Both sides are expected to emit
/// keys in sorted order (the TS side does this via
/// <c>stableStringify</c>; the C# side should serialize equivalently
/// — see <see cref="WriteSortedJson"/>).
///
/// Object key order is normalized regardless (we recurse by name not
/// by position), but numeric-vs-string parity, array ordering, and
/// fp-tolerance handling all need to be controlled deliberately —
/// hence this comparer rather than naive <c>JsonElement</c> equality.
/// </summary>
public sealed class NormalizedJsonComparer
{
    private readonly NormalizedJsonComparisonOptions _options;

    public NormalizedJsonComparer(NormalizedJsonComparisonOptions? options = null)
    {
        _options = options ?? new NormalizedJsonComparisonOptions();
    }

    /// <summary>
    /// Compare two JSON trees and return a list of differences. An
    /// empty list means parity. Each diff carries a JSON-Pointer-like
    /// path so the failure message can point right at the regression.
    /// </summary>
    public IReadOnlyList<JsonDiff> Compare(JsonElement left, JsonElement right)
    {
        List<JsonDiff> diffs = new();
        CompareRecursive(left, right, path: string.Empty, diffs);
        return diffs;
    }

    /// <summary>
    /// Serialize a value with sorted object keys and no indentation —
    /// the canonical "wire" form used by the TS side's
    /// <c>stableStringify</c>. C# producers should funnel through this
    /// before byte-comparing.
    ///
    /// Per Opus-4.8 round-3 review: the default encoder is
    /// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> so
    /// non-ASCII content (café 🎉) and HTML metacharacters
    /// (<c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>) round-trip the same
    /// way JS <c>JSON.stringify</c> emits them — otherwise any byte
    /// comparison with TS output false-fails on realistic document
    /// content. Per GPT-5.5 round-3 review: the
    /// <see cref="JsonSerializerOptions"/> overload lets callers
    /// override casing / naming policy when the C# type model has
    /// PascalCase property names but the wire shape demands snake_case.
    /// </summary>
    public static string WriteSortedJson<T>(T value) =>
        WriteSortedJson(value, options: null);

    /// <summary>
    /// Overload accepting explicit <see cref="JsonSerializerOptions"/>;
    /// see <see cref="WriteSortedJson{T}(T)"/> for context.
    /// </summary>
    public static string WriteSortedJson<T>(T value, JsonSerializerOptions? options)
    {
        JsonSerializerOptions effective = options is null
            ? s_defaultRelaxedOptions
            : EnsureRelaxedEncoder(options);

        string raw = JsonSerializer.Serialize(value, effective);
        using JsonDocument doc = JsonDocument.Parse(raw);
        using MemoryStream ms = new();
        using (Utf8JsonWriter writer = new(ms,
            new JsonWriterOptions { Indented = false, Encoder = effective.Encoder }))
        {
            WriteSortedElement(writer, doc.RootElement);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static readonly JsonSerializerOptions s_defaultRelaxedOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static JsonSerializerOptions EnsureRelaxedEncoder(JsonSerializerOptions src)
    {
        // If the caller already chose an encoder, respect it; otherwise
        // give them the relaxed escape so non-ASCII / HTML metas survive.
        // Either way return a single new instance — the caller's frequency
        // of calls dictates whether THIS allocation is hot; we don't
        // intern arbitrary caller options here.
        if (src.Encoder is not null && !ReferenceEquals(src.Encoder, JavaScriptEncoder.Default))
        {
            return src;
        }
        return new JsonSerializerOptions(src) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    }

    private static void WriteSortedElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty p in element.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(p.Name);
                    WriteSortedElement(writer, p.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteSortedElement(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private void CompareRecursive(JsonElement left, JsonElement right, string path, List<JsonDiff> diffs)
    {
        if (_options.IgnoredPaths.Contains(path))
        {
            return;
        }

        if (left.ValueKind != right.ValueKind)
        {
            // Allowed coercion: number vs numeric-string when caller opted in.
            if (_options.NumericStringEqualsNumber &&
                ((left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.String) ||
                 (left.ValueKind == JsonValueKind.String && right.ValueKind == JsonValueKind.Number)) &&
                NumericStringMatch(left, right))
            {
                return;
            }

            diffs.Add(new JsonDiff(path, $"value-kind: {left.ValueKind} vs {right.ValueKind}",
                left.ToString(), right.ToString()));
            return;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                CompareObjects(left, right, path, diffs);
                break;
            case JsonValueKind.Array:
                CompareArrays(left, right, path, diffs);
                break;
            case JsonValueKind.String:
                string ls = left.GetString() ?? string.Empty;
                string rs = right.GetString() ?? string.Empty;
                if (!string.Equals(ls, rs, StringComparison.Ordinal))
                {
                    diffs.Add(new JsonDiff(path, "string value differs", ls, rs));
                }
                break;
            case JsonValueKind.Number:
                if (!NumbersEqual(left, right))
                {
                    diffs.Add(new JsonDiff(path, "number value differs", left.ToString(), right.ToString()));
                }
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                // ValueKind already matched; primitives are equal.
                break;
            case JsonValueKind.Undefined:
            default:
                diffs.Add(new JsonDiff(path, $"unexpected value-kind: {left.ValueKind}", left.ToString(), right.ToString()));
                break;
        }
    }

    private void CompareObjects(JsonElement left, JsonElement right, string path, List<JsonDiff> diffs)
    {
        Dictionary<string, JsonElement> leftProps = left.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        Dictionary<string, JsonElement> rightProps = right.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

        HashSet<string> allKeys = new(leftProps.Keys, StringComparer.Ordinal);
        allKeys.UnionWith(rightProps.Keys);

        foreach (string key in allKeys.OrderBy(k => k, StringComparer.Ordinal))
        {
            string childPath = path + "/" + EscapePathSegment(key);
            if (_options.IgnoredPaths.Contains(childPath))
            {
                continue;
            }

            bool inLeft = leftProps.TryGetValue(key, out JsonElement lv);
            bool inRight = rightProps.TryGetValue(key, out JsonElement rv);

            if (!inLeft)
            {
                diffs.Add(new JsonDiff(childPath, "missing on left", null, rv.ToString()));
                continue;
            }
            if (!inRight)
            {
                diffs.Add(new JsonDiff(childPath, "missing on right", lv.ToString(), null));
                continue;
            }
            CompareRecursive(lv, rv, childPath, diffs);
        }
    }

    private void CompareArrays(JsonElement left, JsonElement right, string path, List<JsonDiff> diffs)
    {
        int leftLen = left.GetArrayLength();
        int rightLen = right.GetArrayLength();
        if (leftLen != rightLen)
        {
            diffs.Add(new JsonDiff(path, $"array length differs: {leftLen} vs {rightLen}", null, null));
            // Still compare overlapping prefix so the caller can see which entries diverge.
        }

        int min = Math.Min(leftLen, rightLen);
        for (int i = 0; i < min; i++)
        {
            CompareRecursive(left[i], right[i], $"{path}/{i}", diffs);
        }
    }

    private static bool NumbersEqual(JsonElement left, JsonElement right)
    {
        // Try integral first to avoid double-precision noise on whole numbers.
        if (left.TryGetInt64(out long li) && right.TryGetInt64(out long ri))
        {
            return li == ri;
        }
        if (left.TryGetDecimal(out decimal ld) && right.TryGetDecimal(out decimal rd))
        {
            return ld == rd;
        }
        double lf = left.GetDouble();
        double rf = right.GetDouble();
        if (double.IsNaN(lf) && double.IsNaN(rf))
        {
            return true;
        }
        return lf == rf;
    }

    private static bool NumericStringMatch(JsonElement a, JsonElement b)
    {
        JsonElement numEl = a.ValueKind == JsonValueKind.Number ? a : b;
        JsonElement strEl = a.ValueKind == JsonValueKind.String ? a : b;
        string s = strEl.GetString() ?? string.Empty;
        // Per Opus-4.8 round-3 review: use NumberStyles.Float
        // (whitespace/sign/decimal/exponent) instead of
        // NumberStyles.Any. The latter accepts thousands separators,
        // currency symbols, and parenthesized negatives, which JS
        // `Number("1,000")` / `Number("$5")` / `Number("(5)")` all
        // return NaN for — so defaulting to Any would MASK exactly
        // the cell-type drift this opt-in is designed to surface.
        const System.Globalization.NumberStyles JsLikeFloat =
            System.Globalization.NumberStyles.AllowLeadingWhite
            | System.Globalization.NumberStyles.AllowTrailingWhite
            | System.Globalization.NumberStyles.AllowLeadingSign
            | System.Globalization.NumberStyles.AllowDecimalPoint
            | System.Globalization.NumberStyles.AllowExponent;
        return decimal.TryParse(s, JsLikeFloat,
                System.Globalization.CultureInfo.InvariantCulture, out decimal parsedStr)
            && numEl.TryGetDecimal(out decimal num)
            && parsedStr == num;
    }

    private static string EscapePathSegment(string segment) =>
        segment.Replace("~", "~0", StringComparison.Ordinal)
               .Replace("/", "~1", StringComparison.Ordinal);
}

/// <summary>Options that loosen / tighten the parity-diff strictness.</summary>
public sealed record NormalizedJsonComparisonOptions
{
    /// <summary>
    /// JSON-Pointer-like paths to ignore (using <c>/</c> separator,
    /// RFC-6901 escaping). Examples: <c>/version</c>, <c>/sourceFiles</c>.
    /// Empty by default — opt-in masking only.
    /// </summary>
    public IReadOnlySet<string> IgnoredPaths { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Treat a numeric-string ("42") as equal to a JSON number (42).
    /// Off by default; opt in when comparing fixtures where one side
    /// preserves source-cell types and the other unifies to string
    /// (e.g. SheetJS XLSX vs ClosedXML cell-type drift).
    /// </summary>
    public bool NumericStringEqualsNumber { get; init; }
}

/// <summary>A single divergence between two JSON trees.</summary>
public sealed record JsonDiff(string Path, string Reason, string? LeftValue, string? RightValue)
{
    public override string ToString() =>
        $"  {Path}: {Reason}" +
        (LeftValue is null && RightValue is null ? string.Empty :
            $"\n    left:  {LeftValue ?? "<missing>"}\n    right: {RightValue ?? "<missing>"}");
}
