using System.Text.Json;
using System.Text.Json.Serialization;

namespace EvalToolkit.EvalGen.Readers;

/// <summary>
/// System.Text.Json converter for <see cref="DatasetRow"/>.
///
/// <para><b>Why this exists:</b> the default reflection-based
/// serializer would emit the row's surface members (<c>Count</c>,
/// <c>Entries</c>) instead of a JS-style flat object, which would
/// silently break the parity envelopes that the harness compares
/// across runtimes. This converter writes <c>{k1: v1, k2: v2}</c> with
/// the row's insertion order preserved (matching JavaScript
/// <c>JSON.stringify</c> of a plain object) and reads back via the
/// same <see cref="JsonElementConverter"/> the readers use, so a
/// round-trip is loss-free for the value shapes any reader emits.</para>
///
/// <para>Nested values are written using the converter for their
/// runtime type — nested <see cref="DatasetRow"/> via this converter,
/// arrays via the default list converter (which will recurse through
/// any nested rows again), and primitives via the default writers.</para>
/// </summary>
public sealed class DatasetRowJsonConverter : JsonConverter<DatasetRow>
{
    public override DatasetRow Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Defer to the same converter the readers use so the parsed
        // shape (long vs double, nested DatasetRow, lists of object?)
        // matches what JsonReader would have produced from the same
        // text.
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        return JsonElementConverter.ToDatasetRow(doc.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, DatasetRow value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        foreach (KeyValuePair<string, object?> entry in value.Entries)
        {
            writer.WritePropertyName(entry.Key);
            WriteValue(writer, entry.Value, options);
        }
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case string s:
                writer.WriteStringValue(s);
                return;
            case bool b:
                writer.WriteBooleanValue(b);
                return;
            case long l:
                writer.WriteNumberValue(l);
                return;
            case int i:
                writer.WriteNumberValue(i);
                return;
            case double d:
                writer.WriteNumberValue(d);
                return;
            case float f:
                writer.WriteNumberValue(f);
                return;
            case decimal m:
                writer.WriteNumberValue(m);
                return;
            case DatasetRow nested:
                // Recurse via this converter so insertion order is kept.
                new DatasetRowJsonConverter().Write(writer, nested, options);
                return;
            case System.Collections.IDictionary dict:
                // IDictionary implements IEnumerable, so it MUST be
                // handled before the IEnumerable case below; otherwise
                // it serializes as an array of KeyValuePair entries and
                // silently breaks parity envelopes (round-6 finding).
                writer.WriteStartObject();
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    string key = entry.Key switch
                    {
                        string s => s,
                        null => string.Empty,
                        _ => Convert.ToString(entry.Key,
                            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    };
                    writer.WritePropertyName(key);
                    WriteValue(writer, entry.Value, options);
                }
                writer.WriteEndObject();
                return;
            case System.Collections.IEnumerable enumerable when value is not string:
                writer.WriteStartArray();
                foreach (object? item in enumerable)
                {
                    WriteValue(writer, item, options);
                }
                writer.WriteEndArray();
                return;
            default:
                // Fall back to System.Text.Json's reflection-based path
                // for any unknown type. This will throw if the runtime
                // type is unsupported, which is the desired behavior —
                // surfacing the mismatch loudly during parity work.
                JsonSerializer.Serialize(writer, value, value.GetType(), options);
                return;
        }
    }
}
