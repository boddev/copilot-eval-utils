using System.Collections.Generic;

namespace EvalToolkit.UI.Models;

/// <summary>
/// Curated list of model identifiers accepted by the GitHub Copilot CLI
/// (<c>copilot --model &lt;id&gt;</c> / <c>gh copilot ... --model &lt;id&gt;</c>).
///
/// <para>The CLI exposes no scriptable "list models" command (only the
/// interactive <c>/model</c> picker), so the wizard ships a maintained
/// static catalog instead. The provider dropdown is rendered as an
/// <em>editable</em> ComboBox so operators can still type a newer model
/// id that isn't in this list.</para>
/// </summary>
public static class GitHubCopilotModelCatalog
{
    /// <summary>
    /// Well-known GitHub Copilot model identifiers, most-capable first.
    /// Update as GitHub adds/removes models; the editable ComboBox keeps
    /// the UI usable even when this list lags the service.
    /// </summary>
    public static IReadOnlyList<string> KnownModels { get; } = new[]
    {
        "gpt-5.1",
        "gpt-5.1-codex",
        "gpt-5",
        "gpt-5-mini",
        "claude-opus-4.1",
        "claude-sonnet-4.5",
        "claude-sonnet-4",
        "claude-haiku-4.5",
        "gemini-2.5-pro",
        "o3",
        "o4-mini",
    };
}
