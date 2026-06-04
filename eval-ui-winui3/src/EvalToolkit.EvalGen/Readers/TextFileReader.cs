using System.Text.RegularExpressions;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Readers;

/// <summary>
/// Reads plain text and markdown files using the TS
/// <c>readTextFile</c> semantics in
/// <c>eval-gen/src/readers/index.ts</c>:
/// <list type="bullet">
///   <item>Throws if the file is empty after trim (with the exact
///     message <c>Text file is empty: {path}</c>).</item>
///   <item>Normalizes <c>\r\n</c> / <c>\r</c> to <c>\n</c>.</item>
///   <item>Splits sections on <b>either</b> two-or-more consecutive
///     newlines <b>or</b> a markdown heading line
///     <c>^#{1,3}\s</c> (level 1–3 ATX heading boundary).</item>
///   <item>Feeds the resulting sections through
///     <see cref="TextChunker"/> to emit
///     <c>{chunk_number, content, word_count}</c> records.</item>
/// </list>
///
/// The split regex is multiline-anchored: <c>(?m)\n{2,}|(?=^#{1,3}\s)</c>.
/// </summary>
public sealed class TextFileReader : IDatasetReader
{
    /// <summary>
    /// JS source: <c>/\n{2,}|(?=^#{1,3}\s)/m</c>. The lookahead means
    /// the <c>#</c>-heading split keeps the heading text in the
    /// following section rather than consuming the boundary.
    /// </summary>
    private static readonly Regex s_sectionSplit = new(
        @"\n{2,}|(?=^\#{1,3}\s)",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public ReadResult Read(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        // No BOM stripping — TS doesn't strip either. (Output is
        // identical because <c>chunkText</c> .Trim()'s every section,
        // and the heading <c>^#</c> anchor only matters at column 0 of
        // a line where there is by definition no preceding content. We
        // leave the BOM in to keep the parity contract crystal-clear:
        // the C# reader operates on the exact byte stream TS would see.
        byte[] bytes = File.ReadAllBytes(absolutePath);
        string content = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetString(bytes);

        if (content.Trim().Length == 0)
        {
            throw new InvalidDataException($"Text file is empty: {absolutePath}");
        }

        // Normalize CR/CRLF to LF so the multi-newline rule matches.
        string normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal)
                                   .Replace("\r", "\n", StringComparison.Ordinal);

        string[] sections = s_sectionSplit.Split(normalized);
        var records = TextChunker.Chunk(sections);
        return new ReadResult { Records = records, Format = InputFormat.Txt };
    }
}
