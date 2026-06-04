using System.Text.Json;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Readers;

/// <summary>
/// Ports the TS <c>readJson</c> reader in
/// <c>eval-gen/src/readers/index.ts</c>. Accepts either a JSON array
/// of objects (preferred) or a single JSON object (wrapped as a
/// one-element list). Anything else throws to match the TS error
/// message verbatim.
/// </summary>
public sealed class JsonReader : IDatasetReader
{
    public ReadResult Read(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        string content = File.ReadAllText(absolutePath, System.Text.Encoding.UTF8);
        if (content.Length > 0 && content[0] == '\uFEFF')
        {
            content = content.Substring(1);
        }

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
