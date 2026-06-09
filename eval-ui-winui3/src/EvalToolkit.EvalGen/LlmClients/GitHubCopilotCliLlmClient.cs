using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// GitHub Copilot CLI provider. Invokes the standalone <c>copilot</c> CLI in
/// non-interactive mode and feeds the prompt on <b>stdin</b>:
/// <c>copilot --output-format json --no-color [--model X]</c> with the prompt
/// piped to standard input.
///
/// <para>Design notes (see <see cref="CopilotCliLocator"/> and
/// <see cref="CopilotCliOutput"/> for the why):</para>
/// <list type="bullet">
///   <item>The prompt is sent on <b>stdin</b>, not as a <c>-p &lt;prompt&gt;</c>
///         command-line argument. Copilot reads stdin as the prompt when stdin
///         is a non-TTY pipe and no <c>-p</c> is given, which sidesteps the
///         Windows ~32K command-line length limit (CreateProcess / Win32 206).
///         A <c>-p</c> argument overflows that limit for large or wide
///         datasets; stdin has no such cap, so generation works for prompts of
///         any size and matches the behavior of the Node CLI on Unix.</item>
///   <item>Launches the real <c>copilot</c> executable directly
///         (<c>UseShellExecute=false</c>), not the legacy <c>gh copilot</c>
///         extension and not the non-launchable npm shim.</item>
///   <item>Uses <c>--output-format json</c> and extracts the answer from the
///         <c>assistant.message</c> event, which is robust against the startup
///         banners and MCP/tool noise the CLI prints.</item>
///   <item>Does NOT pass <c>--allow-all-tools</c>: these are pure
///         JSON-generation prompts that need no tools, and withholding it
///         avoids giving prompt-injected dataset content tool access.</item>
/// </list>
/// </summary>
public sealed class GitHubCopilotCliLlmClient : ILlmClient
{
    private readonly IProcessRunner _runner;
    private readonly string? _model;
    private readonly string _executable;

    public GitHubCopilotCliLlmClient(LlmClientOptions? options = null, IProcessRunner? runner = null)
    {
        _runner = runner ?? new SystemProcessRunner();
        _model = options?.Model ?? Environment.GetEnvironmentVariable(EnvVars.EvalGenModel);
        _executable = CopilotCliLocator.Resolve();
    }

    public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<T> GenerateStructuredAsync<T>(string prompt, string schemaDescription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(schemaDescription);

        string structuredPrompt = StructuredPromptBuilder.Build(prompt, schemaDescription);

        var args = new List<string>
        {
            "--output-format",
            "json",
            "--no-color",
        };
        if (!string.IsNullOrEmpty(_model))
        {
            args.Add("--model");
            args.Add(_model);
        }

        // The prompt travels on stdin (not a -p argument), so there is no
        // command-line length limit to overflow.
        string output = await _runner.RunAsync(
            new ProcessInvocation(_executable, args, StandardInput: structuredPrompt, UseShell: false),
            cancellationToken).ConfigureAwait(false);

        string assistantText = CopilotCliOutput.ExtractAssistantText(output);
        return StructuredJsonParser.Parse<T>(assistantText);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
