namespace EvalToolkit.Core;

/// <summary>
/// Supported input file formats for evaluation datasets. Mirrors the TS
/// <c>InputFormat</c> union in <c>eval-gen/src/types.ts</c>. The string
/// values are what get written into <c>ReadResult.format</c> by the
/// reader factory; downstream consumers (writers, profilers, parity
/// tests) match on the string, not the enum identifier — so values are
/// stable across the language boundary.
/// </summary>
public enum InputFormat
{
    Csv,
    Json,
    Jsonl,
    Xlsx,
    Docx,
    Pdf,
    Pptx,
    Txt,
}

/// <summary>
/// Helpers for converting <see cref="InputFormat"/> to / from the
/// lowercase string identifier the TS impl uses on the wire.
/// </summary>
public static class InputFormats
{
    /// <summary>The canonical lowercase string identifier for a format.</summary>
    public static string ToWireString(this InputFormat format) => format switch
    {
        InputFormat.Csv => "csv",
        InputFormat.Json => "json",
        InputFormat.Jsonl => "jsonl",
        InputFormat.Xlsx => "xlsx",
        InputFormat.Docx => "docx",
        InputFormat.Pdf => "pdf",
        InputFormat.Pptx => "pptx",
        InputFormat.Txt => "txt",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>
    /// Parse the wire string back into an enum. TSV is treated as CSV
    /// (matches the TS reader behavior — both use the same parser with a
    /// different delimiter and emit <c>format: "csv"</c> downstream).
    /// </summary>
    public static InputFormat FromWireString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant() switch
        {
            "csv" or "tsv" => InputFormat.Csv,
            "json" => InputFormat.Json,
            "jsonl" => InputFormat.Jsonl,
            "xlsx" or "xls" => InputFormat.Xlsx,
            "docx" => InputFormat.Docx,
            "pdf" => InputFormat.Pdf,
            "pptx" => InputFormat.Pptx,
            "txt" or "md" or "markdown" => InputFormat.Txt,
            _ => throw new NotSupportedException($"Unsupported input format: '{value}'"),
        };
    }
}
