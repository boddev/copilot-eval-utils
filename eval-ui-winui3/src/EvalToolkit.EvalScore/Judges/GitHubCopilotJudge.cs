using EvalToolkit.Core;
using EvalToolkit.EvalScore.Models;
using EvalToolkit.EvalScore.Process;

namespace EvalToolkit.EvalScore.Judges;

/// <summary>
/// GitHub Copilot CLI judge. Ports TS <c>GitHubCopilotJudge</c> from
/// <c>eval-score/node/src/judge-providers.ts</c>.
///
/// <para>This judge uses the literal <c>copilot</c> command (NOT the
/// <c>gh copilot</c> subcommand that EvalGen's
/// <c>GitHubCopilotCliLlmClient</c> uses) and a different strict
/// argument list. Operators can override the command entirely by
/// setting <see cref="EnvVars.EvalScoreGithubCopilotCommand"/>; in
/// that case the prompt is written to stdin via the shell path.</para>
///
/// <para>Env reading mirrors TS: <see cref="Model"/> is captured at
/// construction (TS instance-field initialization), but the
/// <see cref="EnvVars.EvalScoreGithubCopilotCommand"/> override is
/// re-read inside <see cref="ScoreAsync"/> (TS reads it inside
/// <c>score()</c> — per round-1 review R2).</para>
/// </summary>
public sealed class GitHubCopilotJudge : IJudge
{
    private const string CliCommand = "copilot";

    // TS error-message prefixes — round-1 review B2 required preservation.
    private const string CliErrorPrefix = "GitHub Copilot CLI judge";
    private const string CommandErrorPrefix = "GitHub Copilot judge command";

    private static readonly string[] s_cliBaseArgs = new[]
    {
        "--silent",
        "--allow-all",
        "--no-custom-instructions",
        "--no-remote",
        "--stream",
        "off",
        "--output-format",
        "text",
    };

    private readonly IProcessRunner _processRunner;

    public GitHubCopilotJudge(IProcessRunner? processRunner = null)
    {
        _processRunner = processRunner ?? new SystemProcessRunner();
        Model = Environment.GetEnvironmentVariable(EnvVars.EvalScoreGithubCopilotModel);
    }

    public JudgeProvider Provider => JudgeProvider.GitHubCopilot;
    public string? Model { get; }

    public async Task<JudgeScore> ScoreAsync(EvalRow row, EvaluatorName evaluator = EvaluatorName.Similarity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        string prompt = ScoringPromptBuilder.Build(row, evaluator, jsonResponse: true);

        // TS reads EVALSCORE_GITHUB_COPILOT_COMMAND inside score() — re-read each call.
        string? commandOverride = Environment.GetEnvironmentVariable(EnvVars.EvalScoreGithubCopilotCommand);
        string output = !string.IsNullOrEmpty(commandOverride)
            ? await RunCommandAsync(commandOverride, prompt, cancellationToken).ConfigureAwait(false)
            : await RunCopilotCliAsync(prompt, cancellationToken).ConfigureAwait(false);

        JudgeScore parsed = JudgeScoreParser.Parse(output);
        return parsed.Model is null ? parsed with { Model = Model } : parsed;
    }

    private Task<string> RunCommandAsync(string command, string prompt, CancellationToken ct)
    {
        // TS: spawn(command, { shell: true, stdio: ['pipe', 'pipe', 'pipe'] });
        // write prompt to stdin, close stdin, capture stdout.
        return _processRunner.RunAsync(
            new ProcessInvocation(
                Command: command,
                Arguments: Array.Empty<string>(),
                StandardInput: prompt,
                UseShell: true,
                ErrorMessagePrefix: CommandErrorPrefix),
            ct);
    }

    private Task<string> RunCopilotCliAsync(string prompt, CancellationToken ct)
    {
        // TS arg list: ['-p', prompt, '--silent', '--allow-all', '--no-custom-instructions',
        //               '--no-remote', '--stream', 'off', '--output-format', 'text'];
        var args = new List<string>(2 + s_cliBaseArgs.Length) { "-p", prompt };
        args.AddRange(s_cliBaseArgs);
        return _processRunner.RunAsync(
            new ProcessInvocation(
                Command: CliCommand,
                Arguments: args,
                StandardInput: null,
                UseShell: false,
                ErrorMessagePrefix: CliErrorPrefix),
            ct);
    }
}
