using EvalToolkit.Core;
using EvalToolkit.EvalGen.LlmClients;

namespace EvalToolkit.EvalGen.Tests.LlmClients;

[Collection("EnvVarSerial")]
public sealed class WorkIQA2ALlmClientAuthLayeringTests : IDisposable
{
    // GPT-5.5 round-2 R3: prove constructor layering — env-token preferred,
    // no-config throws the TS operator-facing setup message.
    private static readonly string[] AuthEnvVars = new[]
    {
        EnvVars.EvalGenWorkIqToken,
        EnvVars.WorkIqA2aAccessToken,
        EnvVars.WorkIqA2aTokenCommand,
        EnvVars.EvalScoreA2aTokenCommand,
        EnvVars.EvalScoreA2aAuthMode,
        EnvVars.WorkIqA2aAuthMode,
        EnvVars.EvalScoreA2aAuth,
        EnvVars.WorkIqA2aAuth,
    };

    private readonly Dictionary<string, string?> _originalValues = new();

    public WorkIQA2ALlmClientAuthLayeringTests()
    {
        foreach (string name in AuthEnvVars)
        {
            _originalValues[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        foreach ((string name, string? value) in _originalValues)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public void NoEvalGenToken_NoFactoryConfig_Throws_OperatorFacingSetupMessage()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            new WorkIQA2ALlmClient(new LlmClientOptions()));
        Assert.Contains("Work IQ A2A requires a delegated bearer token", ex.Message);
        Assert.Contains("EVALGEN_WORKIQ_TOKEN", ex.Message);
        Assert.Contains("WorkIQAgent.Ask", ex.Message);
    }

    [Fact]
    public async Task EvalGenToken_IsPreferred()
    {
        Environment.SetEnvironmentVariable(EnvVars.EvalGenWorkIqToken, "fake-token");
        // No throw implies EVALGEN_WORKIQ_TOKEN wired the StaticToken path
        // before the factory's Noop fallback would have rejected.
        await using var client = new WorkIQA2ALlmClient(new LlmClientOptions());
        Assert.NotNull(client);
    }
}

