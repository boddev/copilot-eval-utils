using System.Text;

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
///   <item>Skip paragraphs that are empty after JS-trim.</item>
///   <item>Flush when <c>chunk.length + piece.length &gt; target AND
///     chunk.length &gt; 0</c>. A single paragraph longer than target
///     still gets emitted on its own without splitting.</item>
///   <item>Output record is <c>{chunk_number:int (1-based),
///     content:string, word_count:int}</c>.</item>
///   <item>Word count: <c>trimmed.split(/\s+/).filter(w =&gt; w.length &gt; 0).length</c>.</item>
/// </list>
///
/// <para>Trim and word-split both go through <see cref="JsCompat"/> to
/// stay byte-equivalent with JS on U+FEFF (BOM) and U+0085 (NEL), which
/// .NET's default <see cref="string.Trim"/> and <c>Regex \s</c>
/// classify differently from ECMAScript.</para>
/// </summary>
internal static class TextChunker
{
    public const int ChunkTargetChars = 500;

    public static List<DatasetRow> Chunk(IEnumerable<string> paragraphs)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);

        var records = new List<DatasetRow>();
        var chunk = new StringBuilder();
        int chunkNum = 1;

        void Flush()
        {
            string trimmed = JsCompat.Trim(chunk.ToString());
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
            string piece = JsCompat.Trim(para);
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
    /// Uses the JS-equivalent whitespace class on
    /// <see cref="JsCompat.WhitespaceRun"/>.
    /// </summary>
    private static int CountWords(string trimmed)
    {
        if (trimmed.Length == 0)
        {
            return 0;
        }
        string[] pieces = JsCompat.WhitespaceRun.Split(trimmed);
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

