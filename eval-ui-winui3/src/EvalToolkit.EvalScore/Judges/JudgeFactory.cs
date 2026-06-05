using EvalToolkit.EvalScore.Models;
using EvalToolkit.EvalScore.Process;
using EvalToolkit.WorkIQ;

namespace EvalToolkit.EvalScore.Judges;

/// <summary>
/// Factory for <see cref="IJudge"/> instances. Mirrors TS
/// <c>createJudge</c> in <c>eval-score/node/src/judge-providers.ts</c>.
///
/// <para>The <see cref="IWorkIQClient"/> parameter is required for
/// <see cref="JudgeProvider.WorkIq"/> and unused for the other
/// providers (TS too — the parameter is positional). For tests, the
/// non-WorkIQ overloads accept an <see cref="IProcessRunner"/> or
/// <see cref="HttpClient"/> to substitute the I/O seam.</para>
/// </summary>
public static class JudgeFactory
{
    public static IJudge Create(
        JudgeProvider provider,
        IWorkIQClient? workIqClient,
        string? tenantId = null,
        string? agentId = null)
    {
        return provider switch
        {
            JudgeProvider.WorkIq => new WorkIQJudge(
                workIqClient ?? throw new ArgumentNullException(nameof(workIqClient),
                    "WorkIQ judge requires a non-null IWorkIQClient."),
                tenantId,
                agentId),
            JudgeProvider.GitHubCopilot => new GitHubCopilotJudge(),
            JudgeProvider.AzureOpenAi => new AzureOpenAIJudge(),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
    }
}
