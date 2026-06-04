namespace EvalToolkit.EvalGen.Writers;

/// <summary>
/// Writes a markdown review file. Mirrors the TS
/// <c>writeReviewMarkdown</c> in <c>eval-gen/src/writers.ts</c>:
///
/// <code>
/// const mdPath = outputPath.replace(/\.(csv|xlsx|json)$/i, '-review.md');
/// const absPath = path.resolve(mdPath);
/// fs.mkdirSync(path.dirname(absPath), { recursive: true });
/// fs.writeFileSync(absPath, content, 'utf-8');
/// </code>
///
/// <para><b>Byte-exact contract pinned by the writers-probe:</b></para>
/// <list type="bullet">
///   <item><b>Path rewrite.</b> The trailing <c>.csv|.xlsx|.json</c>
///     extension (case-insensitive) on <c>outputPath</c> is replaced
///     with the literal <c>-review.md</c> (leading dash, NOT a
///     <c>.review.md</c> dotted extension). If <c>outputPath</c> ends
///     in any other extension or has no extension, it is returned
///     UNCHANGED — the TS writer then overwrites that source file at
///     that exact path (the writers-probe pinned this behavior so the
///     C# port doesn't silently diverge by, say, appending
///     <c>-review.md</c>). Callers that want different fallback
///     semantics must adjust the path before calling.</item>
///   <item><b>Verbatim content.</b> No trailing newline is appended;
///     no encoding declaration is emitted. The body is written as-is
///     in UTF-8 with no BOM.</item>
/// </list>
/// </summary>
public sealed class ReviewMarkdownWriter
{
    /// <summary>
    /// Write <paramref name="content"/> as a review markdown file.
    /// <paramref name="outputPath"/> is path-rewritten as described in
    /// the class doc comment. Returns the absolute path the file was
    /// written to.
    /// </summary>
#pragma warning disable CA1822 // Instance method by design; future-proofs for DI / mocking
    public string Write(string content, string outputPath)
#pragma warning restore CA1822
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        string rewritten = PathRewrite.RewriteExtension(outputPath, "-review.md");
        string absolutePath = Path.GetFullPath(rewritten);

        string? dir = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(absolutePath, content, JsonShape.Utf8NoBom);
        return absolutePath;
    }
}
