using System.Text.Json;

namespace EvalToolkit.EvalGen.Readers;

/// <summary>
/// Converts <see cref="JsonElement"/> trees into the reader's native
/// dynamic-typed representation:
/// <list type="bullet">
///   <item><see cref="JsonValueKind.Object"/> → <see cref="DatasetRow"/>
///     (insertion order preserved).</item>
///   <item><see cref="JsonValueKind.Array"/> →
///     <c>IReadOnlyList&lt;object?&gt;</c>.</item>
///   <item>String / number / bool / null → corresponding CLR type.</item>
/// </list>
/// Mirrors the implicit <c>JSON.parse</c> behavior the TS reader relies
/// on: every numeric becomes a JS <c>number</c>, every string stays a
/// string, every object becomes a key-ordered map. C# distinguishes
/// integral from real numbers explicitly (long vs double) because
/// downstream code (writers, parity comparer) tracks the distinction.
/// </summary>
internal static class JsonElementConverter
{
    public static DatasetRow ToDatasetRow(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Expected JSON object but got {element.ValueKind}.");
        }
        var row = new DatasetRow();
        foreach (JsonProperty prop in element.EnumerateObject())
        {
            row.Set(prop.Name, ConvertValue(prop.Value));
        }
        return row;
    }

    public static object? ConvertValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => ToDatasetRow(element),
            JsonValueKind.Array => ConvertArray(element),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => ConvertNumber(element),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText(),
        };
    }

    private static List<object?> ConvertArray(JsonElement element)
    {
        var list = new List<object?>(element.GetArrayLength());
        foreach (JsonElement child in element.EnumerateArray())
        {
            list.Add(ConvertValue(child));
        }
        return list;
    }

    private static object ConvertNumber(JsonElement element)
    {
        // Preserve integer-vs-real distinction. JS treats every JSON
        // number as `double`, but downstream C# consumers (parity
        // comparer, writers) need to round-trip integers without
        // synthesizing a fractional ".0" suffix. The parity comparer
        // normalizes long ↔ double when the values are equal, so this
        // is safe across the language boundary.
        if (element.TryGetInt64(out long asLong))
        {
            return asLong;
        }
        return element.GetDouble();
    }
}
