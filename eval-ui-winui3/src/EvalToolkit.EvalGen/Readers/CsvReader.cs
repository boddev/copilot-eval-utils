using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Readers;

/// <summary>
/// Reads CSV / TSV files in a way that matches the TS
/// <c>csv-parse/sync</c> reader in <c>eval-gen/src/readers/index.ts</c>
/// with options <c>{ columns: true, skip_empty_lines: true, trim: true }</c>.
///
/// Parity contract pinned by the parity test suite:
/// <list type="bullet">
///   <item>Delimiter is <c>\t</c> when the file extension is
///     <c>.tsv</c>; otherwise <c>,</c> (matches TS
///     <c>filePath.endsWith('.tsv') ? '\t' : ','</c>).</item>
///   <item>The first non-empty row is the header.</item>
///   <item>Empty lines are skipped (do NOT produce a blank record).</item>
///   <item>Every cell value is trimmed and stored as a
///     <see cref="string"/>. <c>csv-parse</c> does NOT auto-coerce
///     numbers, so cells like <c>"42"</c> stay <c>"42"</c> rather than
///     becoming <c>42</c>. The C#-side reader is intentionally strict
///     about this — see <c>NumericStringEqualsNumber</c> in the parity
///     harness for the opt-in cross-runtime comparison flag.</item>
///   <item>RFC4180 quoted fields with embedded delimiters / newlines /
///     escaped double-quotes are handled by CsvHelper's default mode.</item>
///   <item>A UTF-8 BOM at the start of the file is stripped before
///     parsing (csv-parse handles this implicitly via Node's
///     <c>fs.readFileSync(path, 'utf-8')</c> behavior; on .NET we
///     normalize explicitly to avoid the BOM polluting the first
///     header name).</item>
///   <item>Duplicate header names get a <c>_N</c> suffix matching
///     <c>csv-parse</c>'s default <c>columns: true</c> behavior
///     (second occurrence becomes <c>name_1</c>, third
///     <c>name_2</c>, etc.). Tests pin this.</item>
/// </list>
///
/// Note: cell types staying string-typed is the same behavior chosen
/// by TS; XLSX (slice 2) differs and keeps numeric typing because
/// SheetJS does the coercion.
/// </summary>
public sealed class CsvReader : IDatasetReader
{
    public ReadResult Read(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        bool isTsv = absolutePath.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase);
        char delimiterChar = isTsv ? '\t' : ',';

        // Read raw text up front so we can normalize BOM the same way
        // Node's fs.readFileSync('utf-8') does. The .NET StreamReader
        // does detect the BOM, but doing it once explicitly keeps the
        // parser layer dumb.
        string content = File.ReadAllText(absolutePath, System.Text.Encoding.UTF8);
        if (content.Length > 0 && content[0] == '\uFEFF')
        {
            content = content.Substring(1);
        }

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiterChar.ToString(CultureInfo.InvariantCulture),
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.Trim,
            IgnoreBlankLines = true,
            MissingFieldFound = null,
            BadDataFound = null,
        };

        var records = new List<DatasetRow>();
        using var reader = new StringReader(content);
        using var csv = new CsvHelper.CsvReader(reader, config);

        if (!csv.Read())
        {
            return new ReadResult { Records = records, Format = InputFormat.Csv };
        }
        csv.ReadHeader();
        string[]? originalHeaders = csv.HeaderRecord;
        if (originalHeaders is null)
        {
            return new ReadResult { Records = records, Format = InputFormat.Csv };
        }

        // Apply csv-parse duplicate-header suffixing (<name>_N) so two
        // columns named "foo" become ["foo", "foo_1"]. TS produces this
        // shape under the hood and downstream consumers may depend on
        // it; pinning the behavior keeps reader-port byte-exact.
        string[] dedupedHeaders = DedupeHeaders(originalHeaders);

        while (csv.Read())
        {
            var row = new DatasetRow(capacity: dedupedHeaders.Length);
            for (int i = 0; i < dedupedHeaders.Length; i++)
            {
                string rawValue = csv.GetField(i) ?? string.Empty;
                // TrimOptions.Trim already strips leading/trailing
                // whitespace inside quoted fields per RFC4180 + csv-parse.
                row.Set(dedupedHeaders[i], rawValue);
            }
            records.Add(row);
        }

        return new ReadResult
        {
            Records = records,
            Format = InputFormat.Csv,
        };
    }

    /// <summary>
    /// Apply csv-parse default suffixing for duplicate header names:
    /// the first occurrence keeps its name; the Nth occurrence (N &gt;= 2)
    /// becomes <c>{name}_{N - 1}</c>. So <c>["foo", "foo", "foo"]</c>
    /// becomes <c>["foo", "foo_1", "foo_2"]</c>.
    /// </summary>
    private static string[] DedupeHeaders(string[] headers)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new string[headers.Length];
        for (int i = 0; i < headers.Length; i++)
        {
            string name = headers[i] ?? string.Empty;
            if (!seen.TryGetValue(name, out int count))
            {
                seen[name] = 1;
                result[i] = name;
            }
            else
            {
                string deduped = $"{name}_{count}";
                seen[name] = count + 1;
                result[i] = deduped;
            }
        }
        return result;
    }
}
