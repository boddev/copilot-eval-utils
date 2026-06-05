using EvalToolkit.Core;
using EvalToolkit.EvalScore.Judges;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Tests.Judges;

[Collection("EvalScoreEnvVarSerial")]
public class JudgeFactoryTests
{
    private static readonly string[] s_allEnvVars =
    {
        EnvVars.AzureOpenAiEndpoint, EnvVars.AzureAiOpenAiEndpoint,
        EnvVars.AzureOpenAiApiKey, EnvVars.AzureAiApiKey,
        EnvVars.AzureOpenAiApiVersion, EnvVars.AzureAiApiVersion,
        EnvVars.AzureOpenAiDeployment, EnvVars.AzureAiModelName,
        EnvVars.EvalScoreGithubCopilotCommand, EnvVars.EvalScoreGithubCopilotModel,
    };

    [Fact]
    public void WorkIqProvider_ReturnsWorkIQJudge()
    {
        var judge = JudgeFactory.Create(JudgeProvider.WorkIq, new StubWorkIQClient(), tenantId: "t", agentId: "a");
        Assert.IsType<WorkIQJudge>(judge);
        Assert.Equal(JudgeProvider.WorkIq, judge.Provider);
    }

    [Fact]
    public void WorkIqProvider_NullClient_Throws()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => JudgeFactory.Create(JudgeProvider.WorkIq, workIqClient: null));
        Assert.Equal("workIqClient", ex.ParamName);
    }

    [Fact]
    public void GitHubCopilotProvider_ReturnsGitHubCopilotJudge()
    {
        EnvScope.WithoutAll(s_allEnvVars, () =>
        {
            var judge = JudgeFactory.Create(JudgeProvider.GitHubCopilot, workIqClient: null);
            Assert.IsType<GitHubCopilotJudge>(judge);
            Assert.Equal(JudgeProvider.GitHubCopilot, judge.Provider);
        });
    }

    [Fact]
    public void AzureOpenAiProvider_ReturnsAzureOpenAIJudge()
    {
        EnvScope.WithoutAll(s_allEnvVars, () =>
        {
            var judge = JudgeFactory.Create(JudgeProvider.AzureOpenAi, workIqClient: null);
            Assert.IsType<AzureOpenAIJudge>(judge);
            Assert.Equal(JudgeProvider.AzureOpenAi, judge.Provider);
            (judge as IDisposable)?.Dispose();
        });
    }
}
