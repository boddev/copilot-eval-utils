using EvalToolkit.EvalScore.Judges;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Tests.Judges;

public class WorkIQJudgeTests
{
    private static EvalRow Row() => new()
    {
        Prompt = "p",
        ExpectedAnswer = "e",
        SourceLocation = "s",
        ActualAnswer = "a",
    };

    [Fact]
    public async Task NullAgentId_RoutesThroughAskAsync()
    {
        var client = new StubWorkIQClient { AskResponse = "72" };
        var judge = new WorkIQJudge(client, tenantId: "tenant", agentId: null);

        var score = await judge.ScoreAsync(Row());

        Assert.Single(client.AskCalls);
        Assert.Empty(client.AskWithMetadataCalls);
        Assert.Equal("tenant", client.AskCalls[0].TenantId);
        Assert.Equal(72, score.Score);
    }

    [Fact]
    public async Task EmptyAgentId_RoutesThroughAskAsync()
    {
        // Per round-1 review Q4: empty-string agentId is falsy in TS;
        // should route to AskAsync.
        var client = new StubWorkIQClient { AskResponse = "45" };
        var judge = new WorkIQJudge(client, tenantId: null, agentId: string.Empty);

        var score = await judge.ScoreAsync(Row());

        Assert.Single(client.AskCalls);
        Assert.Empty(client.AskWithMetadataCalls);
        Assert.Equal(45, score.Score);
    }

    [Fact]
    public async Task NonEmptyAgentId_RoutesThroughAskWithMetadata()
    {
        var client = new StubWorkIQClient { AskWithMetadataResponse = new("88") };
        var judge = new WorkIQJudge(client, tenantId: "tenant", agentId: "agent-1");

        var score = await judge.ScoreAsync(Row());

        Assert.Empty(client.AskCalls);
        Assert.Single(client.AskWithMetadataCalls);
        Assert.Equal("tenant", client.AskWithMetadataCalls[0].Options?.TenantId);
        Assert.Equal("agent-1", client.AskWithMetadataCalls[0].Options?.AgentId);
        Assert.Equal(88, score.Score);
    }

    [Fact]
    public async Task PromptBuiltFromRow_PassedToClient()
    {
        var client = new StubWorkIQClient { AskResponse = "10" };
        var judge = new WorkIQJudge(client);

        await judge.ScoreAsync(Row(), EvaluatorName.Relevance);

        Assert.Contains("Relevance rubric", client.AskCalls[0].Prompt);
        Assert.Contains("Prompt: p", client.AskCalls[0].Prompt);
    }

    [Fact]
    public void Provider_IsWorkIq()
    {
        var judge = new WorkIQJudge(new StubWorkIQClient());
        Assert.Equal(JudgeProvider.WorkIq, judge.Provider);
        Assert.Null(judge.Model);
    }
}
