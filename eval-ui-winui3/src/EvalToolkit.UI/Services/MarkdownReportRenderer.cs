using System.Net;
using System.Text;
using Markdig;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Renders an EvalScore markdown report into a self-contained HTML
/// document for WebView2 display in the Step 5 results viewer.
///
/// <para><b>Security model (defense in depth — GPT-5.5 plan-review
/// blocker #4).</b>
/// <list type="number">
///   <item><b>Markdown layer:</b> Markdig with raw HTML disabled
///   (<c>DisableHtml()</c>) so user / judge text can't inject
///   <c>&lt;script&gt;</c> directly through Markdown.</item>
///   <item><b>HTML layer:</b> the rendered body is wrapped in a doc
///   carrying a strict CSP meta tag (<c>default-src 'none'</c>,
///   <c>script-src 'none'</c>, <c>style-src 'unsafe-inline'</c> only
///   for the inline stylesheet, everything else blocked). This neuters
///   <c>javascript:</c> URIs and any other resource fetch even if
///   Markdig is somehow tricked into emitting active content.</item>
///   <item><b>WebView2 navigation layer:</b> the caller wires
///   <c>NavigationStarting</c> to cancel any navigation other than
///   the initial <c>NavigateToString</c> load (see
///   <c>WizardView.xaml.cs</c>).</item>
/// </list>
/// </para>
/// </summary>
public static class MarkdownReportRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAdvancedExtensions()
        .Build();

    private const string DefaultStyle = """
        body { font-family: 'Segoe UI Variable', 'Segoe UI', sans-serif; font-size: 14px; line-height: 1.5; margin: 16px; color: #222; background: #fafafa; }
        h1, h2, h3 { font-weight: 600; margin-top: 1.2em; }
        h1 { font-size: 1.7em; border-bottom: 1px solid #ddd; padding-bottom: 0.2em; }
        h2 { font-size: 1.3em; }
        h3 { font-size: 1.1em; }
        table { border-collapse: collapse; margin: 0.5em 0; }
        th, td { border: 1px solid #ddd; padding: 4px 8px; text-align: left; vertical-align: top; }
        th { background: #eee; }
        code { background: #f0f0f0; padding: 1px 4px; border-radius: 3px; font-family: 'Cascadia Mono', Consolas, monospace; }
        pre { background: #f5f5f5; padding: 8px; overflow-x: auto; border-radius: 4px; }
        blockquote { border-left: 3px solid #888; margin: 0; padding: 4px 12px; color: #555; background: #f5f5f5; }
        a { color: #0067c0; }
        """;

    /// <summary>Render the given markdown to a self-contained HTML document.</summary>
    public static string RenderToHtml(string markdown)
    {
        string body = Markdig.Markdown.ToHtml(markdown ?? string.Empty, Pipeline);
        var sb = new StringBuilder(body.Length + 1024);
        sb.Append("""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'none'; style-src 'unsafe-inline'; img-src 'none'; connect-src 'none'; font-src 'none'; object-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'none';">
            <title>Evaluation Report</title>
            <style>
            """);
        sb.AppendLine();
        sb.Append(DefaultStyle);
        sb.AppendLine();
        sb.Append("</style>\n</head>\n<body>\n");
        sb.Append(body);
        sb.Append("\n</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Fallback HTML doc shown when the report file can't be loaded —
    /// just escapes the message and surfaces it; same CSP envelope.
    /// </summary>
    public static string RenderError(string message)
    {
        string escaped = WebUtility.HtmlEncode(message ?? string.Empty);
        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline';">
            <title>Report unavailable</title>
            <style>{DefaultStyle}</style>
            </head>
            <body>
            <h1>Report unavailable</h1>
            <p>{escaped}</p>
            </body>
            </html>
            """;
    }
}
