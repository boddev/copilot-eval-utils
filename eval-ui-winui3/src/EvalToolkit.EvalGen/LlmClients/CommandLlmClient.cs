using System.Text.Json;

namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// Custom command provider. Ports <c>CommandLLMClient</c> from
/// <c>eval-gen/src/llm-client.ts</c>.
///
/// The command receives a single JSON object on stdin
/// (<c>{ "prompt": ..., "schemaDescription": ... }</c>) and must print a
/// JSON object matching the requested schema to stdout. TS uses a shell
/// invocation so users can supply <c>"python my_script.py --flag"</c>.
/// </summary>
public sealed class CommandLlmClient : ILlmClient
{
    private readonly IProcessRunner _runner;
    private readonly string _command;

    public CommandLlmClient(string command, IProcessRunner? runner = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("Command provider requires --llm-command or EVALGEN_LLM_COMMAND");
        }
        _command = command;
        _runner = runner ?? new SystemProcessRunner();
    }

    public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<T> GenerateStructuredAsync<T>(string prompt, string schemaDescription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(schemaDescription);

        string input = JsonSerializer.Serialize(new
        {
            prompt,
            schemaDescription,
        });

        string output = await _runner.RunAsync(
            new ProcessInvocation(_command, Array.Empty<string>(), StandardInput: input, UseShell: true),
            cancellationToken).ConfigureAwait(false);

        return StructuredJsonParser.Parse<T>(output);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
