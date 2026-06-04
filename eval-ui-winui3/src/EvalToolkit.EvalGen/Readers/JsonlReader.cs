using System.Text;
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
/// <para>The file is read as raw bytes and decoded with a UTF-8 encoder
/// that does <b>not</b> auto-detect a leading BOM, so the BOM (if
/// present) survives into the explicit line-1 strip below.
/// <see cref="StreamReader"/>'s default constructors have
/// <c>detectEncodingFromByteOrderMarks: true</c>, which silently eats
/// the BOM at the stream level — the round-6 review caught the original
/// implementation relying on that and rendering the manual strip dead
/// code.</para>
///
/// <para>Line splitting is on <c>\n</c> (with optional <c>\r</c> stripped
/// from each trailing edge) to match the TS <c>content.split('\n')</c>
/// rule. A lone classic-Mac <c>\r</c> separator is <b>not</b> treated as
/// a line break — this matches the TS behavior and the round-6 reviewer
/// note. Parsing errors include the file path and 1-based line number
/// in the thrown message, matching the TS error string format
/// <c>Invalid JSONL in {filePath} at line {N}: {detail}</c>.</para>
/// </summary>
public sealed class JsonlReader : IDatasetReader
{
    public ReadResult Read(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        byte[] bytes = File.ReadAllBytes(absolutePath);
        string content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetString(bytes);

        var records = new List<DatasetRow>();
        // Split only on \n to match the TS content.split('\n') rule —
        // lone \r is NOT a line separator. Trim trailing \r per line to
        // accept Windows CRLF inputs.
        string[] lines = content.Split('\n');
        for (int idx = 0; idx < lines.Length; idx++)
        {
            int lineNumber = idx + 1;
            string line = lines[idx];
            if (line.Length > 0 && line[^1] == '\r')
            {
                line = line[..^1];
            }
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

