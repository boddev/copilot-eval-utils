using EvalToolkit.Core;
using EvalToolkit.EvalScore.Judges;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Tests.Judges;

[Collection("EvalScoreEnvVarSerial")]
public class GitHubCopilotJudgeTests
{
    private static EvalRow Row() => new()
    {
        Prompt = "what",
        ExpectedAnswer = "expected",
        SourceLocation = "src",
        ActualAnswer = "actual",
    };

    [Fact]
    public async Task NoCommandOverride_UsesCopilotCliWithExactArgList()
    {
        EnvScope.WithoutAll(
            new[] { EnvVars.EvalScoreGithubCopilotCommand, EnvVars.EvalScoreGithubCopilotModel },
            () =>
            {
                var runner = new RecordingRunner { Response = "{\"score\": 73}" };
                var judge = new GitHubCopilotJudge(runner);
                var score = judge.ScoreAsync(Row()).GetAwaiter().GetResult();
                Assert.Equal(73, score.Score);

                Assert.Single(runner.Invocations);
                var inv = runner.Invocations[0];
                Assert.Equal("copilot", inv.Command);
                Assert.False(inv.UseShell);
                Assert.Null(inv.StandardInput);
                Assert.Equal("GitHub Copilot CLI judge", inv.ErrorMessagePrefix);

                // TS arg list (exact order):
                Assert.Equal("-p", inv.Arguments[0]);
                Assert.Contains("Prompt: what", inv.Arguments[1]);
                Assert.Equal("--silent", inv.Arguments[2]);
                Assert.Equal("--allow-all", inv.Arguments[3]);
                Assert.Equal("--no-custom-instructions", inv.Arguments[4]);
                Assert.Equal("--no-remote", inv.Arguments[5]);
                Assert.Equal("--stream", inv.Arguments[6]);
                Assert.Equal("off", inv.Arguments[7]);
                Assert.Equal("--output-format", inv.Arguments[8]);
                Assert.Equal("text", inv.Arguments[9]);
                Assert.Equal(10, inv.Arguments.Count);
            });
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CommandOverride_RoutesThroughShellWithPromptOnStdin()
    {
        EnvScope.Without(EnvVars.EvalScoreGithubCopilotCommand, EnvVars.EvalScoreGithubCopilotModel, () =>
        {
            EnvScope.Set(EnvVars.EvalScoreGithubCopilotCommand, "my-judge --flag", () =>
            {
                var runner = new RecordingRunner { Response = "{\"score\": 50}" };
                var judge = new GitHubCopilotJudge(runner);
                judge.ScoreAsync(Row()).GetAwaiter().GetResult();

                var inv = runner.Invocations.Single();
                Assert.Equal("my-judge --flag", inv.Command);
                Assert.True(inv.UseShell);
                Assert.NotNull(inv.StandardInput);
                Assert.Contains("Prompt: what", inv.StandardInput);
                Assert.Empty(inv.Arguments);
                Assert.Equal("GitHub Copilot judge command", inv.ErrorMessagePrefix);
            });
        });
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ModelEnv_StampedOnScoreWhenJsonOmitsModel()
    {
        EnvScope.Without(EnvVars.EvalScoreGithubCopilotCommand, EnvVars.EvalScoreGithubCopilotModel, () =>
        {
            EnvScope.Set(EnvVars.EvalScoreGithubCopilotModel, "gpt-5-preview", () =>
            {
                var runner = new RecordingRunner { Response = "{\"score\": 88}" };
                var judge = new GitHubCopilotJudge(runner);
                Assert.Equal("gpt-5-preview", judge.Model);

                var score = judge.ScoreAsync(Row()).GetAwaiter().GetResult();
                Assert.Equal("gpt-5-preview", score.Model);
            });
        });
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ParsedModel_TakesPrecedenceOverEnv()
    {
        EnvScope.Without(EnvVars.EvalScoreGithubCopilotCommand, EnvVars.EvalScoreGithubCopilotModel, () =>
        {
            EnvScope.Set(EnvVars.EvalScoreGithubCopilotModel, "env-model", () =>
            {
                var runner = new RecordingRunner { Response = "{\"score\": 80, \"model\": \"resp-model\"}" };
                var judge = new GitHubCopilotJudge(runner);
                var score = judge.ScoreAsync(Row()).GetAwaiter().GetResult();
                Assert.Equal("resp-model", score.Model);
            });
        });
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CommandEnvIsReadAtScoreTime_NotCtor()
    {
        // Per round-1 review R2: ctor reads model; ScoreAsync reads command.
        EnvScope.WithoutAll(
            new[] { EnvVars.EvalScoreGithubCopilotCommand, EnvVars.EvalScoreGithubCopilotModel },
            () =>
            {
                var runner = new RecordingRunner { Response = "{\"score\": 1}" };
                // Construct judge BEFORE env var is set.
                var judge = new GitHubCopilotJudge(runner);
                EnvScope.Set(EnvVars.EvalScoreGithubCopilotCommand, "after-ctor-cmd", () =>
                {
                    judge.ScoreAsync(Row()).GetAwaiter().GetResult();
                });
                Assert.True(runner.Invocations[0].UseShell);
                Assert.Equal("after-ctor-cmd", runner.Invocations[0].Command);
            });
        await Task.CompletedTask;
    }

    [Fact]
    public void Provider_IsGitHubCopilot()
    {
        EnvScope.WithoutAll(
            new[] { EnvVars.EvalScoreGithubCopilotCommand, EnvVars.EvalScoreGithubCopilotModel },
            () =>
            {
                var judge = new GitHubCopilotJudge(new RecordingRunner());
                Assert.Equal(JudgeProvider.GitHubCopilot, judge.Provider);
            });
    }
}
