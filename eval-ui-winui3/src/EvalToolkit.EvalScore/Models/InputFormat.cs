namespace EvalToolkit.EvalScore.Models;

/// <summary>
/// Input file formats supported by EvalScore. Mirrors the TS
/// <c>InputFormat</c> union (<c>'csv' | 'tsv' | 'xlsx' | 'json'</c>)
/// in <c>eval-score/node/src/types.ts</c>.
/// </summary>
public enum InputFormat
{
    Csv,
    Tsv,
    Xlsx,
    Json,
}

public static class InputFormats
{
    public static string ToWireString(this InputFormat format) => format switch
    {
        InputFormat.Csv => "csv",
        InputFormat.Tsv => "tsv",
        InputFormat.Xlsx => "xlsx",
        InputFormat.Json => "json",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    public static InputFormat FromWireString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant() switch
        {
            "csv" => InputFormat.Csv,
            "tsv" => InputFormat.Tsv,
            "xlsx" => InputFormat.Xlsx,
            "json" => InputFormat.Json,
            _ => throw new NotSupportedException($"Unknown input format: '{value}'"),
        };
    }
}
