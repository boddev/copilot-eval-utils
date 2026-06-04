using System.Text.Json;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Readers;

/// <summary>
/// Ports the TS <c>readJson</c> reader in
/// <c>eval-gen/src/readers/index.ts</c>. Accepts either a JSON array
/// of objects (preferred) or a single JSON object (wrapped as a
/// one-element list). Anything else throws to match the TS error
/// message verbatim.
///
/// <para><b>BOM handling:</b> a UTF-8 BOM is NOT stripped — TS
/// <c>JSON.parse</c> throws on BOM-prefixed input and so does
/// <see cref="JsonDocument.Parse(string)"/>. This is verified by both
/// the round-5 reviewer probes and by a unit test
/// (<c>JsonReader_BOM_Throws_MatchingTS</c>).</para>
/// </summary>
public sealed class JsonReader : IDatasetReader
{
    public ReadResult Read(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        // Read raw bytes and decode as UTF-8 without BOM auto-detection
        // (see CsvReader for the same pattern + rationale): TS
        // JSON.parse throws on a BOM-prefixed input, and so does
        // JsonDocument.Parse on the same byte sequence. Using
        // File.ReadAllText(path, encoding) would silently strip the
        // BOM and accept the file, diverging from TS.
        byte[] bytes = File.ReadAllBytes(absolutePath);
        string content = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetString(bytes);

        using JsonDocument doc = JsonDocument.Parse(content);
        var records = new List<DatasetRow>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in doc.RootElement.EnumerateArray())
            {
                records.Add(JsonElementConverter.ToDatasetRow(element));
            }
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            records.Add(JsonElementConverter.ToDatasetRow(doc.RootElement));
        }
        else
        {
            // Same error text TS throws (readJson line ~74).
            throw new InvalidDataException("JSON file must contain an array of objects or a single object");
        }

        return new ReadResult { Records = records, Format = InputFormat.Json };
    }
}
