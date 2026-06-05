using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// GitHub Copilot CLI provider. Ports <c>GitHubCopilotCliClient</c> from
/// <c>eval-gen/src/llm-client.ts</c>.
///
/// Invokes <c>gh copilot -- -p &lt;prompt&gt; --silent --no-color [--model X]</c>
/// without a shell so existing GitHub Copilot authentication is reused
/// and only the model response lands on stdout.
/// </summary>
public sealed class GitHubCopilotCliLlmClient : ILlmClient
{
    private readonly IProcessRunner _runner;
    private readonly string? _model;

    public GitHubCopilotCliLlmClient(LlmClientOptions? options = null, IProcessRunner? runner = null)
    {
        _runner = runner ?? new SystemProcessRunner();
        _model = options?.Model ?? Environment.GetEnvironmentVariable(EnvVars.EvalGenModel);
    }

    public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<T> GenerateStructuredAsync<T>(string prompt, string schemaDescription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(schemaDescription);

        var args = new List<string>
        {
            "copilot",
            "--",
            "-p",
            StructuredPromptBuilder.Build(prompt, schemaDescription),
            "--silent",
            "--no-color",
        };
        if (!string.IsNullOrEmpty(_model))
        {
            args.Add("--model");
            args.Add(_model);
        }

        string output = await _runner.RunAsync(
            new ProcessInvocation("gh", args, StandardInput: null, UseShell: false),
            cancellationToken).ConfigureAwait(false);

        return StructuredJsonParser.Parse<T>(output);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
