using System.Text.Json;
using System.Text.Json.Serialization;

namespace EvalToolkit.Core;

/// <summary>
/// JSON converter that reads and writes <see cref="Assertion"/> values in
/// the same shape the TypeScript implementation uses:
/// <code>
/// { "type": "must_contain", "value": "Acme" }
/// { "type": "must_contain_any", "values": ["foo","bar"] }
/// { "type": "must_not_contain", "value": "internal" }
/// </code>
///
/// Without this converter, <c>System.Text.Json</c>'s polymorphic
/// serialization would emit a different shape (using <c>$type</c>), which
/// would break byte-exact parity with the TS writers.
/// </summary>
public sealed class AssertionJsonConverter : JsonConverter<Assertion>
{
    public override Assertion? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Assertion must be a JSON object.");
        }

        if (!root.TryGetProperty("type", out JsonElement typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Assertion is missing the required `type` discriminator.");
        }

        string typeTag = typeElement.GetString() ?? string.Empty;
        return typeTag switch
        {
            "must_contain" => new MustContainAssertion
            {
                Value = root.GetProperty("value").GetString()
                    ?? throw new JsonException("must_contain assertion is missing `value`."),
                WholeWord = root.TryGetProperty("wholeWord", out JsonElement w) && w.GetBoolean(),
            },
            "must_contain_any" => new MustContainAnyAssertion
            {
                Values = ReadStringArray(root, "values"),
            },
            "must_not_contain" => new MustNotContainAssertion
            {
                Value = root.GetProperty("value").GetString()
                    ?? throw new JsonException("must_not_contain assertion is missing `value`."),
            },
            _ => throw new JsonException($"Unknown assertion type: '{typeTag}'."),
        };
    }

    public override void Write(Utf8JsonWriter writer, Assertion value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteString("type", value.TypeTag);
        switch (value)
        {
            case MustContainAssertion mc:
                writer.WriteString("value", mc.Value);
                if (mc.WholeWord)
                {
                    writer.WriteBoolean("wholeWord", true);
                }
                break;
            case MustContainAnyAssertion mca:
                writer.WriteStartArray("values");
                foreach (string v in mca.Values)
                {
                    writer.WriteStringValue(v);
                }
                writer.WriteEndArray();
                break;
            case MustNotContainAssertion mnc:
                writer.WriteString("value", mnc.Value);
                break;
            default:
                throw new JsonException($"Unsupported assertion subtype: {value.GetType().Name}");
        }
        writer.WriteEndObject();
    }

    private static List<string> ReadStringArray(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"Expected JSON array for `{propertyName}`.");
        }
        List<string> result = [];
        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"Expected string element in `{propertyName}` array.");
            }
            result.Add(item.GetString()!);
        }
        return result;
    }
}
