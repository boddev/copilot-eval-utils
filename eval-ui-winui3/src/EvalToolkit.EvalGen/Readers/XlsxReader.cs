using ClosedXML.Excel;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Readers;

/// <summary>
/// Reads <c>.xlsx</c> spreadsheet files to mirror the TS
/// <c>readXlsx</c> reader in <c>eval-gen/src/readers/index.ts</c>:
///
/// <code>
/// const wb = XLSX.readFile(filePath);
/// const sheetName = wb.SheetNames[0];
/// if (!sheetName) throw new Error('XLSX file has no sheets');
/// return XLSX.utils.sheet_to_json(wb.Sheets[sheetName]);
/// </code>
///
/// <para>SheetJS <c>sheet_to_json</c> with default options has a small
/// pile of subtle behaviors that diverge from both csv-parse and from
/// the obvious "iterate cells with ClosedXML" approach. All rules
/// below were verified empirically against the <c>xlsx</c> npm package
/// at slice-2 pre-flight (reviewer round 6 ground-truth dump). Read
/// the inline test fixtures in <c>XlsxReaderTests</c> for the exact
/// node probes each rule was derived from.</para>
///
/// <list type="bullet">
///   <item><b>First sheet only.</b> Other sheets are ignored. An empty
///     workbook (zero sheets) throws <c>'XLSX file has no sheets'</c> —
///     <b>byte-exact</b> with the TS message.</item>
///   <item><b>Row 1 = headers.</b> Cell values are coerced to string.
///     Header order is taken from the column range; gaps in the header
///     row produce synthetic <c>__EMPTY</c> / <c>__EMPTY_1</c> / ...
///     keys (different from an explicit empty-string header which
///     stays as the empty-string key).</item>
///   <item><b>Duplicate header names get <c>_1</c> / <c>_2</c>
///     suffixes</b> starting at the <b>second</b> occurrence (first
///     stays bare): <c>[name, name]</c> → <c>[name, name_1]</c>,
///     <c>[a, b, a]</c> → <c>[a, b, a_1]</c>. This is the
///     <b>opposite</b> of csv-parse's collapse rule — slice 1's
///     <see cref="CsvReader"/> deliberately did NOT generalize, so the
///     two formats keep their own rules.</item>
///   <item><b>Missing data cells are sparse.</b> A row of
///     <c>[1, ,3]</c> against header <c>[a,b,c]</c> emits
///     <c>{a:1, c:3}</c> — the <c>b</c> field is <b>omitted</b>, not
///     emitted as <c>""</c> or <c>null</c>. The CSV trailing-comma
///     case (which DOES emit <c>""</c>) is intentionally different.</item>
///   <item><b>Empty-string cells are preserved.</b> A cell containing
///     the empty string is kept as <c>""</c>; only a truly missing /
///     blank cell triggers the sparse-omit rule above.</item>
///   <item><b>All-missing rows are omitted.</b> A row that produces
///     zero fields after the missing-cell omission is dropped entirely
///     (no empty object emitted).</item>
///   <item><b>Native numeric typing.</b> Numbers stay numbers (no CSV
///     all-strings coercion). Integral values within the
///     <see cref="long"/> range are returned as <c>long</c>, others as
///     <c>double</c> — matches the same long/double split
///     <see cref="JsonElementConverter"/> uses for JSON numbers, so
///     the parity envelope shape stays uniform across formats.</item>
///   <item><b>Booleans stay native.</b></item>
///   <item><b>Dates are Excel serial numbers</b> (not <c>DateTime</c>).
///     SheetJS without <c>cellDates:true</c> emits the raw serial,
///     which is what the TS reader does. Conversion uses the
///     Excel-1900 epoch (Dec 30 1899) — see <see cref="ToExcelSerial"/>.</item>
///   <item><b>Formula cells use the cached value</b>, not the formula
///     text.</item>
///   <item><b>Error cells are treated as missing</b> (omit-as-sparse).
///     A row containing only error cells therefore disappears
///     entirely — matches SheetJS.</item>
///   <item><b>Ragged-long rows synthesize <c>__EMPTY</c> headers.</b>
///     A row with more cells than the header row produces extra fields
///     named <c>__EMPTY</c>, <c>__EMPTY_1</c>, ... — counter-intuitive
///     and a frequent source of bugs when ports try to derive their
///     own dispatch rule.</item>
/// </list>
///
/// <para><b>.xls (BIFF8) is not supported.</b> ClosedXML wraps
/// DocumentFormat.OpenXml which only handles the modern <c>.xlsx</c>
/// XML format. The TS impl supports both via SheetJS. Until a follow-
/// up reader-port slice adds a BIFF8 dependency (NPOI is the usual
/// choice), <c>.xls</c> is rejected upstream by
/// <see cref="DatasetReader"/>.</para>
///
/// <para><b>Known residual: accounting-format header keys.</b>
/// Verified empirically (round-8 N2 probe matrix, 8 number-format
/// strings × SheetJS SSF vs ClosedXML <c>GetFormattedString</c>):
/// 7/8 formats match byte-exact, but the accounting format
/// <c>_($* #,##0.00_)</c> diverges by exactly one space character —
/// SheetJS yields <c>" $1,234.50 "</c> while ClosedXML yields
/// <c>" $ 1,234.50 "</c>. The divergence is in ClosedXML's
/// implementation of the <c>*</c> repeat-fill operator (which inserts
/// one extra alignment space) and cannot be worked around without
/// reimplementing SSF locally. Pinned via
/// <c>Read_AccountingFormatHeader_IsKnownByOneSpaceDivergence</c>.
/// Accounting-formatted HEADER cells are vanishingly rare in eval
/// datasets, so the divergence is accepted rather than worked around.</para>
///
/// <para><b>Known residual: all-empty-string rows/columns.</b>
/// ClosedXML's <c>RangeUsed()</c> (and every
/// <c>XLCellsUsedOptions</c> variant — verified empirically) excludes
/// cells whose only content is the empty string when there are no
/// adjacent non-empty cells in the same row/column. As a result, a
/// pathological sheet whose row 1 contains ONLY empty-string cells
/// would have its row-1 dropped from the bounding box, and row 2
/// would be misinterpreted as the header — diverging from SheetJS
/// which would treat row 1 as a header of <c>["", "_1", ...]</c>.
/// Mixed rows (any row containing at least one non-empty-string cell)
/// behave correctly; the limitation only fires when an ENTIRE row or
/// column is composed exclusively of empty strings, which is
/// vanishingly rare in real eval datasets. Documented here rather
/// than worked around because the workaround (reading the worksheet
/// dimension from the raw XML, since ClosedXML does not surface it
/// via the public API) would significantly complicate the reader for
/// negligible real-world benefit.</para>
/// </summary>
public sealed class XlsxReader : IDatasetReader
{
    /// <summary>
    /// Excel's "epoch" for date serial 0 is December 30, 1899 — chosen
    /// so that serial 1 = Jan 1 1900 even though Excel believes 1900
    /// was a leap year (it wasn't; serial 60 = the bogus Feb 29 1900).
    /// For any real date on or after Mar 1 1900,
    /// <c>(date - epoch).TotalDays</c> gives the correct serial.
    /// </summary>
    private static readonly DateTime s_excelEpoch = new(1899, 12, 30);

    /// <summary>
    /// Synthetic header key SheetJS uses for missing cells in row 1.
    /// First missing column gets <c>__EMPTY</c>, subsequent ones get
    /// <c>__EMPTY_1</c>, <c>__EMPTY_2</c>, ... (note the suffix index
    /// starts at 1, not 0, on the SECOND missing column — matching the
    /// dup-name suffix rule).
    /// </summary>
    private const string SyntheticEmptyKey = "__EMPTY";

    public ReadResult Read(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        using var workbook = new XLWorkbook(absolutePath);
        if (workbook.Worksheets.Count == 0)
        {
            // Byte-exact match for the TS error message.
            throw new InvalidDataException("XLSX file has no sheets");
        }

        IXLWorksheet sheet = workbook.Worksheet(1);
        var records = BuildRecords(sheet);

        return new ReadResult
        {
            Records = records,
            Format = InputFormat.Xlsx,
        };
    }

    private static List<DatasetRow> BuildRecords(IXLWorksheet sheet)
    {
        var records = new List<DatasetRow>();

        IXLRange? used = sheet.RangeUsed();
        if (used is null)
        {
            // Empty sheet (no data anywhere) — SheetJS returns []
            // rather than throwing.
            return records;
        }

        int firstRow = used.FirstRow().RowNumber();
        int lastRow = used.LastRow().RowNumber();
        int firstCol = used.FirstColumn().ColumnNumber();
        int lastCol = used.LastColumn().ColumnNumber();

        // Pre-compute the GLOBAL max column across header + all data
        // rows. SheetJS computes its key namespace once per sheet
        // (covering header row width + the maximum data-row overshoot),
        // and then any row that overshoots uses the same precomputed
        // synthetic keys. We mirror that by walking the data rows once
        // up-front to find the global right edge.
        int globalLastCol = lastCol;
        for (int rowIdx = firstRow + 1; rowIdx <= lastRow; rowIdx++)
        {
            globalLastCol = Math.Max(globalLastCol, GetRowLastUsedColumn(sheet, rowIdx, lastCol));
        }

        // Header row is the first row of the used range. SheetJS treats
        // row 1 as the header regardless of where data starts; ClosedXML
        // gives us the first NON-EMPTY row via RangeUsed, which matches
        // SheetJS's effective behavior for the tabular dispatch path.
        // The returned array spans firstCol..globalLastCol; positions
        // past the header row's lastCol default to the synthetic
        // __EMPTY key (subject to the unified collision-resolution
        // process below).
        string[] headers = BuildHeaders(sheet, firstRow, firstCol, lastCol, globalLastCol);

        // Header-only sheet (no data rows below) → no records, matching
        // SheetJS probe (header-only XLSX → []).
        if (lastRow <= firstRow)
        {
            return records;
        }

        for (int rowIdx = firstRow + 1; rowIdx <= lastRow; rowIdx++)
        {
            var row = new DatasetRow(capacity: headers.Length);

            for (int col = firstCol; col <= globalLastCol; col++)
            {
                IXLCell cell = sheet.Cell(rowIdx, col);
                if (IsMissing(cell))
                {
                    continue;
                }

                int headerOffset = col - firstCol;
                string key = headers[headerOffset];
                row.Set(key, ConvertCellValue(cell));
            }

            // SheetJS omits rows that produced zero fields (all-missing
            // / error-only rows). Anything with at least one entry —
            // including an empty-string field — is kept.
            if (row.Count > 0)
            {
                records.Add(row);
            }
        }

        return records;
    }

    /// <summary>
    /// Build the header-name array using SheetJS's row-1 rule, extended
    /// to cover all column positions up to <paramref name="globalLastCol"/>
    /// (header row width + maximum data-row overshoot). The resolution
    /// algorithm is a single unified pass that handles every collision
    /// uniformly:
    /// <list type="number">
    ///   <item>Build a "desired name" per column position:
    ///     <list type="bullet">
    ///       <item>Within the header row: <c>HeaderToString(cell)</c> if
    ///         present, else <c>__EMPTY</c>.</item>
    ///       <item>Past the header row: always <c>__EMPTY</c>
    ///         (synthetic key for an overshoot position).</item>
    ///     </list></item>
    ///   <item>For each desired name in column order, if it is already in
    ///     the used-set find the smallest <c>N ≥ 1</c> such that
    ///     <c>name_N</c> is not in the used-set; assign that.</item>
    /// </list>
    ///
    /// <para>Verified against SheetJS probes:
    /// <list type="bullet">
    ///   <item><c>[__EMPTY, ∅, __EMPTY]</c> →
    ///     <c>[__EMPTY, __EMPTY_1, __EMPTY_2]</c></item>
    ///   <item><c>[∅, __EMPTY, ∅]</c> →
    ///     <c>[__EMPTY, __EMPTY_1, __EMPTY_2]</c></item>
    ///   <item><c>[__EMPTY_1, ∅, ∅]</c> →
    ///     <c>[__EMPTY_1, __EMPTY, __EMPTY_2]</c></item>
    ///   <item><c>[a, a, a]</c> → <c>[a, a_1, a_2]</c></item>
    ///   <item><c>[a, a, a_1]</c> → <c>[a, a_1, a_1_1]</c> (the 3rd
    ///     column's desired <c>a_1</c> is taken, so it becomes
    ///     <c>a_1_1</c>; this is SheetJS's actual behavior)</item>
    ///   <item><c>[a, __EMPTY]</c> with a 4-col data row →
    ///     <c>[a, __EMPTY, __EMPTY_1, __EMPTY_2]</c></item>
    ///   <item><c>['', '']</c> → <c>['', '_1']</c> (empty-string headers
    ///     dup-suffix the same way)</item>
    /// </list></para>
    /// </summary>
    private static string[] BuildHeaders(IXLWorksheet sheet, int headerRow, int firstCol, int headerLastCol, int globalLastCol)
    {
        int width = globalLastCol - firstCol + 1;
        var desired = new string[width];
        for (int i = 0; i < width; i++)
        {
            int col = firstCol + i;
            if (col > headerLastCol)
            {
                desired[i] = SyntheticEmptyKey;
                continue;
            }
            IXLCell cell = sheet.Cell(headerRow, col);
            desired[i] = IsMissing(cell) ? SyntheticEmptyKey : HeaderToString(cell);
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new string[width];
        for (int i = 0; i < width; i++)
        {
            string baseName = desired[i];
            if (used.Add(baseName))
            {
                resolved[i] = baseName;
                continue;
            }
            int n = 1;
            string candidate;
            do
            {
                candidate = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{baseName}_{n}");
                n++;
            } while (!used.Add(candidate));
            resolved[i] = candidate;
        }
        return resolved;
    }

    /// <summary>
    /// Largest used column number in <paramref name="rowIdx"/>, or
    /// <paramref name="fallbackLastCol"/> if the row has no used cells.
    /// </summary>
    private static int GetRowLastUsedColumn(IXLWorksheet sheet, int rowIdx, int fallbackLastCol)
    {
        IXLRow row = sheet.Row(rowIdx);
        IXLCell? last = row.LastCellUsed();
        if (last is null)
        {
            return fallbackLastCol;
        }
        return Math.Max(fallbackLastCol, last.Address.ColumnNumber);
    }

    /// <summary>
    /// True if a cell is "missing" in SheetJS terms — no value at all,
    /// or holds an error sentinel. An empty-STRING cell is NOT
    /// missing; it's a real empty-string value that SheetJS preserves.
    ///
    /// <para><b>Discriminator choice (empirically verified, slice-2):</b>
    /// We use <c>cell.Value.IsBlank</c> and <c>cell.Value.Type</c> rather
    /// than <c>cell.IsEmpty()</c> or <c>cell.DataType</c>, because:
    /// <list type="bullet">
    /// <item><c>IsEmpty()</c> returns <c>true</c> for an explicit
    /// empty-string text cell (ClosedXML treats Text+<c>""</c> as
    /// "no useful display content"), conflating it with truly-missing
    /// cells. SheetJS preserves the distinction (empty string → kept;
    /// missing → omitted), so we must too.</item>
    /// <item><c>cell.DataType</c> reports <c>Blank</c> for formula
    /// cells whose cached value is numeric (verified probe:
    /// <c>=1+1</c> → <c>DataType=Blank</c>, <c>Value.Type=Number</c>,
    /// <c>Value=2</c>). Branching on <c>DataType</c> would treat such
    /// formula cells as missing.</item>
    /// <item><c>cell.Value</c> resolves formula cells to their cached
    /// value and exposes <c>Type</c> / <c>IsBlank</c> consistently
    /// across literal and formula cells, so it is the single
    /// authoritative discriminator.</item>
    /// </list></para>
    /// </summary>
    private static bool IsMissing(IXLCell cell)
    {
        XLCellValue v = cell.Value;
        if (v.IsBlank)
        {
            return true;
        }
        if (v.Type == XLDataType.Error)
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Coerce a header cell to the string SheetJS would use as the
    /// object key. Empty string is allowed (preserved as the empty
    /// key).
    ///
    /// <para><b>Formatting parity (round-7 finding):</b> SheetJS derives
    /// header keys from the cell's <i>formatted text</i> (the <c>w</c>
    /// property in SheetJS's cell model), not from the raw numeric
    /// value. For a date cell formatted <c>yyyy-mm-dd</c> with serial
    /// 45000, SheetJS returns the key <c>"2023-03-15"</c>; for a
    /// number cell formatted <c>0.00</c> holding the value 5, SheetJS
    /// returns the key <c>"5.00"</c>. Empirically verified probe vs.
    /// the SheetJS <c>xlsx</c> package.</para>
    ///
    /// <para>ClosedXML's <see cref="IXLCell.GetFormattedString()"/>
    /// applies the cell's number-format string (or the workbook's
    /// default for unformatted cells), which agrees with SheetJS for
    /// the common ISO-ish formats and plain numeric output. Exotic
    /// custom formats and locale-sensitive output (currency,
    /// thousands separators) are a known residual where ClosedXML's
    /// formatter may diverge from SheetJS's SSF — tests should pin
    /// invariant-culture cases.</para>
    ///
    /// <para>Note the asymmetry vs. <see cref="ConvertCellValue"/>:
    /// data cells use the RAW serial / numeric value (so a date data
    /// cell becomes its Excel serial number, not its formatted text);
    /// only header cells use the formatted string. This mirrors
    /// SheetJS exactly.</para>
    /// </summary>
    private static string HeaderToString(IXLCell cell)
    {
        XLCellValue v = cell.Value;
        return v.Type switch
        {
            XLDataType.Text => v.GetText(),
            _ => cell.GetFormattedString(),
        };
    }

    /// <summary>
    /// Convert a data-cell value to its parity-equivalent runtime type:
    /// strings stay strings, booleans stay booleans, numbers go through
    /// the same long-vs-double split <see cref="JsonElementConverter"/>
    /// uses, and dates serialize as Excel serial numbers. Reads from
    /// <see cref="IXLCell.Value"/> so formula cells resolve to their
    /// cached value.
    /// </summary>
    private static object? ConvertCellValue(IXLCell cell)
    {
        XLCellValue v = cell.Value;
        switch (v.Type)
        {
            case XLDataType.Text:
                return v.GetText();
            case XLDataType.Boolean:
                return v.GetBoolean();
            case XLDataType.Number:
                return ConvertNumber(v.GetNumber());
            case XLDataType.DateTime:
                return ToExcelSerial(v.GetDateTime());
            case XLDataType.TimeSpan:
                return v.GetTimeSpan().TotalDays;
            default:
                // Error / Blank already filtered upstream; fall back to
                // the formatted text for any unexpected type.
                return cell.GetFormattedString();
        }
    }

    /// <summary>
    /// Mirror <see cref="JsonElementConverter"/>'s long-vs-double rule
    /// so XLSX numbers and JSON numbers serialize identically in the
    /// parity envelope: integral values within the
    /// <see cref="long"/> range become <c>long</c>, everything else
    /// stays as <c>double</c>.
    /// </summary>
#pragma warning disable CA1859 // CA1859 doesn't see the boxed long branch — the polymorphic object is intentional here.
    private static object ConvertNumber(double d)
    {
        if (double.IsFinite(d)
            && d >= long.MinValue
            && d <= long.MaxValue
            && d == Math.Floor(d))
        {
            return (long)d;
        }
        return d;
    }
#pragma warning restore CA1859

    /// <summary>
    /// Convert a CLR <see cref="DateTime"/> to its Excel serial number
    /// using the Excel-1900 epoch (Dec 30 1899). For dates on or after
    /// 1900-03-01 this matches Excel exactly. For dates before that
    /// (which Excel models with a fictitious Feb 29 1900) the serial
    /// will be off by 1; in practice dataset files don't carry
    /// pre-1900 dates and SheetJS itself inherits the same quirk.
    /// </summary>
    internal static double ToExcelSerial(DateTime dt)
    {
        return (dt - s_excelEpoch).TotalDays;
    }
}
