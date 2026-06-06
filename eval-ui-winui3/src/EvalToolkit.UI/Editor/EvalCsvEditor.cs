using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EvalToolkit.UI.Editor;

/// <summary>
/// Read/write helper specifically for the 4-column EvalScore CSV
/// (<c>prompt, expected_answer, source_location, actual_answer</c>)
/// from the in-app row editor (slice 25, winui-step4).
///
/// <para>Uses a dedicated whitespace-preserving CSV parser
/// (<see cref="EditorCsvParser"/>) rather than the parity-locked
/// <c>EvalToolkit.EvalGen.Readers.CsvReader</c> — that reader trims
/// every cell to match <c>csv-parse</c>'s default options, which would
/// silently mutate hand-edited prompts/answers across a load+save
/// round-trip. The editor must preserve the user's bytes exactly.
/// </para>
///
/// <para>Write replicates
/// <see cref="EvalToolkit.EvalGen.Writers.EvalCsvWriter"/>'s byte-exact
/// contract: <c>\n</c> terminator (including trailing), no BOM,
/// minimal RFC-4180 quoting only when a field contains
/// <c>,</c> / <c>"</c> / CR / LF.</para>
///
/// <para>Save is atomic: write to <c>{path}.tmp</c> then
/// <see cref="File.Move(string, string, bool)"/> with overwrite=true,
/// the same pattern used by <c>JobMetadataStore.Write</c>. Throws
/// <see cref="IOException"/> if the destination is locked (e.g. Excel
/// has the file open); the caller is expected to surface this.</para>
/// </summary>
public static class EvalCsvEditor
{
    public const string PromptColumn = "prompt";
    public const string ExpectedAnswerColumn = "expected_answer";
    public const string SourceLocationColumn = "source_location";
    public const string ActualAnswerColumn = "actual_answer";

    public static IReadOnlyList<EvalRowRecord> ReadFlat(string csvPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csvPath);

        // UTF-8 read without BOM stripping; preserves any BOM in the
        // first header cell so headers compare correctly when we strip
        // it manually below.
        byte[] bytes = File.ReadAllBytes(csvPath);
        string content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes);

        var records = EditorCsvParser.Parse(content);
        if (records.Count == 0)
        {
            return Array.Empty<EvalRowRecord>();
        }

        // First row is header. Strip leading BOM on first header only.
        var headers = records[0];
        if (headers.Count > 0 && headers[0].Length > 0 && headers[0][0] == '\uFEFF')
        {
            headers[0] = headers[0].Substring(1);
        }

        int promptIdx = FindColumn(headers, PromptColumn);
        int expectedIdx = FindColumn(headers, ExpectedAnswerColumn);
        int sourceIdx = FindColumn(headers, SourceLocationColumn);
        int actualIdx = FindColumn(headers, ActualAnswerColumn);

        var rows = new List<EvalRowRecord>(records.Count - 1);
        for (int i = 1; i < records.Count; i++)
        {
            var r = records[i];
            rows.Add(new EvalRowRecord(
                Prompt: SafeGet(r, promptIdx),
                ExpectedAnswer: SafeGet(r, expectedIdx),
                SourceLocation: SafeGet(r, sourceIdx),
                ActualAnswer: SafeGet(r, actualIdx)));
        }
        return rows;
    }

    public static void WriteFlat(string csvPath, IReadOnlyList<EvalRowRecord> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csvPath);
        ArgumentNullException.ThrowIfNull(rows);

        string absolute = Path.GetFullPath(csvPath);

        var sb = new StringBuilder();
        sb.Append(PromptColumn).Append(',')
          .Append(ExpectedAnswerColumn).Append(',')
          .Append(SourceLocationColumn).Append(',')
          .Append(ActualAnswerColumn).Append('\n');

        foreach (var r in rows)
        {
            AppendField(sb, r.Prompt);
            sb.Append(',');
            AppendField(sb, r.ExpectedAnswer);
            sb.Append(',');
            AppendField(sb, r.SourceLocation);
            sb.Append(',');
            AppendField(sb, r.ActualAnswer);
            sb.Append('\n');
        }

        string? dir = Path.GetDirectoryName(absolute);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string temp = absolute + ".tmp";
        try
        {
            File.WriteAllText(temp, sb.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, absolute, overwrite: true);
        }
        catch
        {
            // Clean up temp on failure so retries don't accumulate
            // {path}.tmp orphans. Don't surface a delete failure if
            // present; the outer throw is the actionable error.
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch { /* best-effort */ }
            throw;
        }
    }

    private static int FindColumn(List<string> headers, string name)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            if (string.Equals(headers[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private static string SafeGet(List<string> row, int idx) =>
        idx < 0 || idx >= row.Count ? string.Empty : row[idx];

    private static void AppendField(StringBuilder sb, string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return;

        bool quote = false;
        foreach (char c in raw)
        {
            if (c == ',' || c == '"' || c == '\n' || c == '\r')
            {
                quote = true;
                break;
            }
        }
        if (!quote)
        {
            sb.Append(raw);
            return;
        }
        sb.Append('"');
        foreach (char c in raw)
        {
            if (c == '"') sb.Append("\"\"");
            else sb.Append(c);
        }
        sb.Append('"');
    }
}

/// <summary>
/// Minimal RFC-4180 CSV parser used by the row editor. Preserves
/// every byte inside cells (no trim). Recognizes both <c>\n</c> and
/// <c>\r\n</c> as record separators. Treats <c>""</c> inside a quoted
/// field as a literal quote.
/// </summary>
internal static class EditorCsvParser
{
    public static List<List<string>> Parse(string content)
    {
        var records = new List<List<string>>();
        if (string.IsNullOrEmpty(content)) return records;

        var current = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        int i = 0;
        int len = content.Length;

        while (i < len)
        {
            char c = content[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < len && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }
                    inQuotes = false;
                    i++;
                    continue;
                }
                field.Append(c);
                i++;
                continue;
            }

            if (c == '"' && field.Length == 0)
            {
                inQuotes = true;
                i++;
                continue;
            }

            if (c == ',')
            {
                current.Add(field.ToString());
                field.Clear();
                i++;
                continue;
            }

            if (c == '\r')
            {
                // Treat \r\n and bare \r as record terminators.
                current.Add(field.ToString());
                field.Clear();
                records.Add(current);
                current = new List<string>();
                i++;
                if (i < len && content[i] == '\n') i++;
                continue;
            }

            if (c == '\n')
            {
                current.Add(field.ToString());
                field.Clear();
                records.Add(current);
                current = new List<string>();
                i++;
                continue;
            }

            field.Append(c);
            i++;
        }

        // Flush trailing field/record (no terminator on last row).
        if (field.Length > 0 || current.Count > 0)
        {
            current.Add(field.ToString());
            records.Add(current);
        }

        return records;
    }
}

public sealed record EvalRowRecord(
    string Prompt,
    string ExpectedAnswer,
    string SourceLocation,
    string ActualAnswer);

