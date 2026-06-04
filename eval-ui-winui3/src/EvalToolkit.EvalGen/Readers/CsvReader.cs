using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Readers;

/// <summary>
/// Reads CSV / TSV files matching the TS <c>csv-parse/sync</c> reader
/// in <c>eval-gen/src/readers/index.ts</c> with options
/// <c>{ columns: true, skip_empty_lines: true, trim: true }</c>.
///
/// Parity contract pinned by the parity test suite — these rules were
/// verified empirically against <c>csv-parse/sync</c> in node (see
/// reviewer round-5 ground-truth probes):
/// <list type="bullet">
///   <item>Delimiter is <c>\t</c> when the file extension is
///     <c>.tsv</c>; otherwise <c>,</c>.</item>
///   <item>The first non-empty row is the header. Headers are trimmed
///     (TrimOptions.Trim applies to every field, including headers).</item>
///   <item>Empty lines are skipped (no blank record emitted).</item>
///   <item>Every cell value is trimmed and stored as a
///     <see cref="string"/> (no numeric coercion — see
///     <c>NumericStringEqualsNumber</c> in the parity harness for the
///     opt-in cross-runtime comparison flag).</item>
///   <item>RFC4180 quoted fields (embedded delimiters / newlines /
///     escaped double-quotes) handled via CsvHelper's default mode.</item>
///   <item><b>BOM is NOT stripped</b>: a leading <c>\uFEFF</c> stays
///     attached to the first header name. Verified against csv-parse —
///     <c>parse('\uFEFFid,name\n…')</c> yields keys
///     <c>["\uFEFFid", "name"]</c>. Excel's "CSV UTF-8" export writes a
///     BOM, so this divergence used to break parity on every Excel CSV
///     in the real eval-prompt fixtures.</item>
///   <item><b>Duplicate headers collapse, last-value-wins</b>: a
///     header row of <c>a,b,a</c> with data <c>1,2,3</c> produces
///     <c>{a:"3", b:"2"}</c> — two keys, in first-occurrence position,
///     and the value of the surviving key is taken from the
///     rightmost-most column with that name. This matches csv-parse's
///     default <c>columns:true</c> behavior. The earlier suffixing
///     <c>foo_1</c>/<c>foo_2</c> scheme was a parity bug.</item>
///   <item><b>Empty header names are preserved</b>: a header row of
///     <c>a,,b</c> with data <c>1,2,3</c> produces
///     <c>{a:"1", "":"2", b:"3"}</c> — the empty string is a valid
///     key. csv-parse retains it; the reader does too.</item>
/// </list>
/// </summary>
public sealed class CsvReader : IDatasetReader
{
    public ReadResult Read(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        bool isTsv = absolutePath.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase);
        char delimiterChar = isTsv ? '\t' : ',';

        // Read raw bytes and decode as UTF-8 without BOM auto-detection.
        // The File.ReadAllText(path, encoding) helper internally uses
        // StreamReader which strips the BOM during detection, regardless
        // of the encoding's encoderShouldEmitUTF8Identifier setting.
        // We need the BOM to remain in the first header so we go bytes →
        // chars manually.
        byte[] bytes = File.ReadAllBytes(absolutePath);
        string content = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetString(bytes);

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiterChar.ToString(CultureInfo.InvariantCulture),
            HasHeaderRecord = false, // We parse headers manually so we can
                                      // replicate csv-parse's collapse-on-dup
                                      // behavior.
            TrimOptions = TrimOptions.Trim,
            IgnoreBlankLines = true,
            MissingFieldFound = null,
            BadDataFound = null,
        };

        var records = new List<DatasetRow>();
        using var reader = new StringReader(content);
        using var parser = new CsvParser(reader, config);

        if (!parser.Read())
        {
            return new ReadResult { Records = records, Format = InputFormat.Csv };
        }
        string[] headers = parser.Record ?? Array.Empty<string>();

        while (parser.Read())
        {
            string[] row = parser.Record ?? Array.Empty<string>();
            var record = new DatasetRow(capacity: headers.Length);

            // Last-value-wins: iterate left-to-right and call Set, which
            // overwrites in place when the key is already present. This
            // means duplicate header keys collapse to a single slot at
            // the FIRST occurrence position with the value of the
            // RIGHTMOST column carrying that name — exactly what
            // csv-parse(columns:true) produces.
            for (int i = 0; i < headers.Length; i++)
            {
                string key = headers[i] ?? string.Empty;
                string value = i < row.Length ? (row[i] ?? string.Empty) : string.Empty;
                record.Set(key, value);
            }
            records.Add(record);
        }

        return new ReadResult
        {
            Records = records,
            Format = InputFormat.Csv,
        };
    }
}
