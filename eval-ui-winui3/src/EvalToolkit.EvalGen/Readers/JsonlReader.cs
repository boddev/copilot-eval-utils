using System.Text.Json;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Readers;

/// <summary>
/// Ports the TS <c>readJsonl</c> reader in
/// <c>eval-gen/src/readers/index.ts</c>. One JSON object per line;
/// blank/whitespace-only lines are skipped; a UTF-8 BOM is stripped
/// only from line 1 (matches the TS impl which does
/// <c>currentLineNumber === 1 ? line.replace(/^\uFEFF/, '') : line</c>).
///
/// Parsing errors include the file path and 1-based line number in the
/// thrown message, matching the TS error string format
/// <c>Invalid JSONL in {filePath} at line {N}: {detail}</c>.
/// </summary>
public sealed class JsonlReader : IDatasetReader
{
    public ReadResult Read(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        var records = new List<DatasetRow>();
        using var stream = File.OpenRead(absolutePath);
        // Use UTF-8 explicitly so the BOM (if any) survives into the
        // first-line strip below — StreamReader with default detection
        // would silently consume it.
        using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false));
        int lineNumber = 0;
        while (reader.ReadLine() is { } rawLine)
        {
            lineNumber++;
            string line = rawLine;
            if (lineNumber == 1 && line.Length > 0 && line[0] == '\uFEFF')
            {
                line = line.Substring(1);
            }
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }
            try
            {
                using JsonDocument doc = JsonDocument.Parse(trimmed);
                records.Add(JsonElementConverter.ToDatasetRow(doc.RootElement));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"Invalid JSONL in {absolutePath} at line {lineNumber}: {ex.Message}",
                    ex);
            }
        }

        return new ReadResult { Records = records, Format = InputFormat.Jsonl };
    }
}
