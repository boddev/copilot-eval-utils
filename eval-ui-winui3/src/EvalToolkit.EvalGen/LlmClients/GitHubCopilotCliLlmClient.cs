using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// GitHub Copilot CLI provider. Invokes the standalone <c>copilot</c> CLI in
/// non-interactive mode:
/// <c>copilot -p &lt;prompt&gt; --output-format json --no-color [--model X]</c>.
///
/// <para>Design notes (see <see cref="CopilotCliLocator"/> and
/// <see cref="CopilotCliOutput"/> for the why):</para>
/// <list type="bullet">
///   <item>Launches the real <c>copilot</c> executable directly
///         (<c>UseShellExecute=false</c>), not the legacy <c>gh copilot</c>
///         extension and not the non-launchable npm shim.</item>
///   <item>Uses <c>--output-format json</c> and extracts the answer from the
///         <c>assistant.message</c> event, which is robust against the startup
///         banners and MCP/tool noise the CLI prints.</item>
///   <item>Does NOT pass <c>--allow-all-tools</c>: these are pure
///         JSON-generation prompts that need no tools, and withholding it
///         avoids giving prompt-injected dataset content tool access.</item>
///   <item>The prompt travels on the command line, which the OS limits to
///         ~32K chars. <see cref="MaxPromptChars"/> lets the pipeline batch
///         large stages; a hard preflight guard turns any remaining overflow
///         into a clear, actionable error instead of a Win32 crash.</item>
/// </list>
/// </summary>
public sealed class GitHubCopilotCliLlmClient : ILlmClient, IPromptSizeLimited
{
    // Windows caps a process command line at 32767 chars. Reserve headroom for
    // the executable path, the other flags, the model name and quoting.
    private const int CommandLineHardLimit = 31000;

    private readonly IProcessRunner _runner;
    private readonly string? _model;
    private readonly string _executable;

    public GitHubCopilotCliLlmClient(LlmClientOptions? options = null, IProcessRunner? runner = null)
    {
        _runner = runner ?? new SystemProcessRunner();
        _model = options?.Model ?? Environment.GetEnvironmentVariable(EnvVars.EvalGenModel);
        _executable = CopilotCliLocator.Resolve();
    }

    /// <summary>
    /// Budget for the <c>prompt</c> argument before
    /// <see cref="StructuredPromptBuilder"/> wraps it. Kept below
    /// <see cref="CommandLineHardLimit"/> to leave room for the wrapper, schema
    /// hint, flags and executable path.
    /// </summary>
    public int MaxPromptChars => 28000;

    public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<T> GenerateStructuredAsync<T>(string prompt, string schemaDescription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(schemaDescription);

        string structuredPrompt = StructuredPromptBuilder.Build(prompt, schemaDescription);
        if (structuredPrompt.Length > CommandLineHardLimit)
        {
            throw new InvalidOperationException(
                $"The generated prompt is {structuredPrompt.Length:N0} characters, which exceeds the GitHub Copilot CLI " +
                $"command-line limit (~{CommandLineHardLimit:N0}). Reduce the number of questions or the dataset size, " +
                "or use the Azure OpenAI provider for very large datasets.");
        }

        var args = new List<string>
        {
            "-p",
            structuredPrompt,
            "--output-format",
            "json",
            "--no-color",
        };
        if (!string.IsNullOrEmpty(_model))
        {
            args.Add("--model");
            args.Add(_model);
        }

        string output = await _runner.RunAsync(
            new ProcessInvocation(_executable, args, StandardInput: null, UseShell: false),
            cancellationToken).ConfigureAwait(false);

        string assistantText = CopilotCliOutput.ExtractAssistantText(output);
        return StructuredJsonParser.Parse<T>(assistantText);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
