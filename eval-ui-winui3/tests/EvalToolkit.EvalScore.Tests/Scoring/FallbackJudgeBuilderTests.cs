using EvalToolkit.Core;
using EvalToolkit.EvalScore.Models;
using EvalToolkit.EvalScore.Scoring;
using EvalToolkit.EvalScore.Tests.Judges;

namespace EvalToolkit.EvalScore.Tests.Scoring;

[Collection("EvalScoreEnvVarSerial")]
public class FallbackJudgeBuilderTests : IDisposable
{
    private readonly string? _originalDisable;
    private readonly string? _originalProvider;

    public FallbackJudgeBuilderTests()
    {
        _originalDisable = Environment.GetEnvironmentVariable(EnvVars.EvalScoreDisableGithubFallback);
        _originalProvider = Environment.GetEnvironmentVariable(EnvVars.EvalScoreFallbackJudgeProvider);
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreDisableGithubFallback, null);
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreFallbackJudgeProvider, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreDisableGithubFallback, _originalDisable);
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreFallbackJudgeProvider, _originalProvider);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Returns_null_for_non_workiq_primary()
    {
        var primary = new EvalToolkit.EvalScore.Judges.AzureOpenAIJudge();
        var result = FallbackJudgeBuilder.Build(primary, null, null, null, false);
        Assert.Null(result);
    }

    [Fact]
    public void Returns_null_when_caller_disables_fallback()
    {
        var primary = new EvalToolkit.EvalScore.Judges.WorkIQJudge(new StubWorkIQClient(), null);
        Assert.Null(FallbackJudgeBuilder.Build(primary, new StubWorkIQClient(), null, null, disableFallback: true));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("TRUE")]
    public void Env_disables_fallback(string envValue)
    {
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreDisableGithubFallback, envValue);
        var primary = new EvalToolkit.EvalScore.Judges.WorkIQJudge(new StubWorkIQClient(), null);
        Assert.Null(FallbackJudgeBuilder.Build(primary, new StubWorkIQClient(), null, null, false));
    }

    [Fact]
    public void Defaults_to_github_copilot_when_no_env_or_caller_provider()
    {
        var primary = new EvalToolkit.EvalScore.Judges.WorkIQJudge(new StubWorkIQClient(), null);
        var result = FallbackJudgeBuilder.Build(primary, new StubWorkIQClient(), null, null, false);
        Assert.NotNull(result);
        Assert.Equal(JudgeProvider.GitHubCopilot, result!.Provider);
    }

    [Fact]
    public void Env_can_select_azure_openai_provider()
    {
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreFallbackJudgeProvider, "azure-openai");
        Environment.SetEnvironmentVariable(EnvVars.AzureOpenAiEndpoint, "https://test.openai.azure.com");
        Environment.SetEnvironmentVariable(EnvVars.AzureOpenAiApiKey, "k");
        Environment.SetEnvironmentVariable(EnvVars.AzureOpenAiDeployment, "gpt-4");
        try
        {
            var primary = new EvalToolkit.EvalScore.Judges.WorkIQJudge(new StubWorkIQClient(), null);
            var result = FallbackJudgeBuilder.Build(primary, new StubWorkIQClient(), null, null, false);
            Assert.NotNull(result);
            Assert.Equal(JudgeProvider.AzureOpenAi, result!.Provider);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVars.AzureOpenAiEndpoint, null);
            Environment.SetEnvironmentVariable(EnvVars.AzureOpenAiApiKey, null);
            Environment.SetEnvironmentVariable(EnvVars.AzureOpenAiDeployment, null);
        }
    }

    [Fact]
    public void Caller_provider_overrides_env_setting()
    {
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreFallbackJudgeProvider, "azure-openai");
        var primary = new EvalToolkit.EvalScore.Judges.WorkIQJudge(new StubWorkIQClient(), null);
        var result = FallbackJudgeBuilder.Build(primary, new StubWorkIQClient(), null, JudgeProvider.GitHubCopilot, false);
        Assert.NotNull(result);
        Assert.Equal(JudgeProvider.GitHubCopilot, result!.Provider);
    }
}
