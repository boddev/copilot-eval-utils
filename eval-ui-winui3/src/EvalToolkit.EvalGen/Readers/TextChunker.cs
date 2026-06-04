using System.Text;
using System.Text.RegularExpressions;

namespace EvalToolkit.EvalGen.Readers;

/// <summary>
/// Ports the TS <c>chunkText(paragraphs)</c> helper in
/// <c>eval-gen/src/readers/index.ts</c>. Greedy fixed-target packing:
/// each chunk accumulates trimmed paragraphs joined by <c>\n</c> until
/// adding the next paragraph would push the current chunk past
/// <see cref="ChunkTargetChars"/>; at that point the current chunk is
/// flushed (provided it contains anything) and the new paragraph
/// starts a fresh chunk.
///
/// Exact rules from the TS source:
/// <list type="bullet">
///   <item>Skip paragraphs that are empty after trim.</item>
///   <item>Flush when <c>chunk.length + piece.length &gt; target AND
///     chunk.length &gt; 0</c>. A single paragraph longer than target
///     still gets emitted on its own without splitting.</item>
///   <item>Output record is <c>{chunk_number:int (1-based),
///     content:string, word_count:int}</c>.</item>
///   <item>Word count: <c>trimmed.split(/\s+/).filter(w =&gt; w.length &gt; 0).length</c>.</item>
/// </list>
/// </summary>
internal static class TextChunker
{
    public const int ChunkTargetChars = 500;

    /// <summary>
    /// Whitespace class equivalent to JavaScript <c>\s</c> at the level
    /// that matters for word counting:
    /// <c>[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]</c>.
    ///
    /// <para>Empirical divergence from .NET <c>Regex \s</c> (verified
    /// round 5): U+FEFF (BOM) is whitespace in JS but a <b>word
    /// character</b> in .NET (so JS counts 2 tokens / .NET counts 1
    /// without this class), and U+0085 (NEL) is the opposite (whitespace
    /// in .NET, word character in JS, so JS counts 1 token / .NET counts
    /// 2). Pinning the class explicitly removes both divergences.</para>
    /// </summary>
    private static readonly Regex s_whitespaceRun = new(
        @"[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static List<DatasetRow> Chunk(IEnumerable<string> paragraphs)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);

        var records = new List<DatasetRow>();
        var chunk = new StringBuilder();
        int chunkNum = 1;

        void Flush()
        {
            string trimmed = chunk.ToString().Trim();
            if (trimmed.Length == 0)
            {
                return;
            }
            int wordCount = CountWords(trimmed);
            var row = new DatasetRow(capacity: 3);
            row.Set("chunk_number", chunkNum);
            row.Set("content", trimmed);
            row.Set("word_count", wordCount);
            records.Add(row);
            chunkNum++;
            chunk.Clear();
        }

        foreach (string para in paragraphs)
        {
            string piece = para.Trim();
            if (piece.Length == 0)
            {
                continue;
            }
            if (chunk.Length + piece.Length > ChunkTargetChars && chunk.Length > 0)
            {
                Flush();
            }
            if (chunk.Length > 0)
            {
                chunk.Append('\n');
            }
            chunk.Append(piece);
        }
        Flush();
        return records;
    }

    /// <summary>
    /// Word count compatible with TS
    /// <c>trimmed.split(/\s+/).filter(w =&gt; w.length &gt; 0).length</c>.
    /// Uses an explicit JS-equivalent whitespace class
    /// (<see cref="s_whitespaceRun"/>) rather than .NET <c>Regex \s</c>
    /// because the two diverge on U+FEFF and U+0085 — see the doc
    /// comment on <see cref="s_whitespaceRun"/> for the exact codepoints
    /// and reviewer-verified counts.
    /// </summary>
    private static int CountWords(string trimmed)
    {
        if (trimmed.Length == 0)
        {
            return 0;
        }
        string[] pieces = s_whitespaceRun.Split(trimmed);
        int count = 0;
        foreach (string p in pieces)
        {
            if (p.Length > 0)
            {
                count++;
            }
        }
        return count;
    }
}
