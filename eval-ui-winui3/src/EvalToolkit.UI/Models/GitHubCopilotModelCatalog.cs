using System.Collections.Generic;

namespace EvalToolkit.UI.Models;

/// <summary>
/// Model identifiers accepted by the GitHub Copilot CLI's <c>--model</c>
/// flag (<c>copilot --model &lt;id&gt;</c>), mirroring the choices shown in
/// the CLI's interactive <c>/model</c> picker.
///
/// <para>The Copilot CLI exposes no scriptable "list models" command (only
/// the interactive picker), and the set is account/entitlement specific, so
/// the wizard ships this maintained list. The field is rendered as an
/// <em>editable</em> ComboBox so an operator whose account exposes a model
/// not listed here (for example a preview or internal-only model) can still
/// type its id. <c>auto</c> lets Copilot choose the model automatically.</para>
///
/// <para>These are the short slugs the CLI expects (for example
/// <c>gpt-5.5</c>, <c>claude-sonnet-4.6</c>) — NOT the <c>publisher/name</c>
/// ids from the separate GitHub Models catalog (<c>models.github.ai</c>),
/// which is a different product and does not match the Copilot CLI.</para>
/// </summary>
public static class GitHubCopilotModelCatalog
{
    /// <summary>
    /// Known GitHub Copilot CLI model slugs. Keep in sync with the CLI's
    /// <c>/model</c> picker; the editable ComboBox keeps the UI usable when
    /// this list lags the service or an account exposes extra models.
    /// </summary>
    public static IReadOnlyList<string> KnownModels { get; } = new[]
    {
        "auto",
        "claude-sonnet-4.6",
        "claude-sonnet-4.5",
        "claude-haiku-4.5",
        "claude-opus-4.8",
        "claude-opus-4.7",
        "claude-opus-4.6",
        "claude-opus-4.5",
        "gpt-5.5",
        "gpt-5.4",
        "gpt-5.3-codex",
        "gpt-5.4-mini",
        "gpt-5-mini",
        "gemini-3.1-pro-preview",
        "gemini-3.5-flash",
    };
}
