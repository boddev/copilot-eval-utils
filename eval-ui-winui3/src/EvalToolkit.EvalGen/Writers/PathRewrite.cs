using System.Text.RegularExpressions;

namespace EvalToolkit.EvalGen.Writers;

/// <summary>
/// Shared path-rewrite helpers used by <see cref="SidecarJsonWriter"/>
/// and <see cref="ReviewMarkdownWriter"/>. Both writers transform an
/// outputPath by replacing a trailing <c>.csv|.xlsx|.json</c> extension
/// (case-insensitive) with a fixed suffix. The TS implementations use:
///
/// <code>
/// outputPath.replace(/\.(csv|xlsx|json)$/i, '.evalgen.json')   // sidecar
/// outputPath.replace(/\.(csv|xlsx|json)$/i, '-review.md')      // markdown
/// </code>
///
/// <para><b>Behavior pinned by the writers probe:</b></para>
/// <list type="bullet">
///   <item>Matching extension → replaced with the supplied suffix.</item>
///   <item>Mixed-case extension (e.g. <c>.CSV</c>, <c>.JSON</c>) → also
///     replaced (regex is case-insensitive).</item>
///   <item>Non-matching extension (e.g. <c>.txt</c>) → path returned
///     UNCHANGED. The TS writers then overwrite the source file at that
///     exact path — preserved here so callers see the same byte-on-disk
///     outcome as the TS tool.</item>
///   <item>No extension at all → path returned UNCHANGED.</item>
/// </list>
/// </summary>
internal static class PathRewrite
{
    private static readonly Regex s_rewriteRegex = new(
        @"\.(csv|xlsx|json)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Replace a trailing <c>.csv|.xlsx|.json</c> extension on
    /// <paramref name="outputPath"/> with <paramref name="newSuffix"/>.
    /// If no recognized extension is present, returns
    /// <paramref name="outputPath"/> unchanged.
    /// </summary>
    public static string RewriteExtension(string outputPath, string newSuffix)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(newSuffix);
        return s_rewriteRegex.Replace(outputPath, newSuffix);
    }
}
