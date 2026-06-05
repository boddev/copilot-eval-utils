using EvalToolkit.Core;
using EvalToolkit.EvalScore.Judges;
using EvalToolkit.EvalScore.Models;
using EvalToolkit.WorkIQ;

namespace EvalToolkit.EvalScore.Scoring;

/// <summary>
/// Picks a fallback judge for the primary judge. Mirrors TS
/// <c>createDefaultFallbackJudge</c> in
/// <c>eval-score/node/src/scorer.ts</c>.
///
/// <para>Rules (preserve exactly):
/// <list type="number">
///   <item>Only the WorkIQ primary judge gets an automatic fallback —
///     non-WorkIQ primaries return <c>null</c>.</item>
///   <item>The caller can force "no fallback" via
///     <see cref="ScoreOptions.DisableFallbackJudge"/> or env
///     <c>EVALSCORE_DISABLE_GITHUB_FALLBACK</c> = <c>1</c>/<c>true</c>/<c>yes</c>.</item>
///   <item>The provider is, in priority order:
///     caller-supplied → env <c>EVALSCORE_FALLBACK_JUDGE_PROVIDER</c>
///     (only azure-openai/github-copilot honored) → github-copilot.</item>
/// </list></para>
/// </summary>
public static class FallbackJudgeBuilder
{
    public static IJudge? Build(
        IJudge primary,
        IWorkIQClient? workIqClient,
        string? tenantId,
        JudgeProvider? configuredProvider,
        bool disableFallback)
    {
        ArgumentNullException.ThrowIfNull(primary);
        if (primary.Provider != JudgeProvider.WorkIq)
        {
            return null;
        }
        if (disableFallback)
        {
            return null;
        }
        string? envDisable = Environment.GetEnvironmentVariable(EnvVars.EvalScoreDisableGithubFallback)?.ToLowerInvariant();
        if (envDisable is "1" or "true" or "yes")
        {
            return null;
        }

        JudgeProvider provider;
        if (configuredProvider.HasValue)
        {
            provider = configuredProvider.Value;
        }
        else
        {
            string? envProvider = Environment.GetEnvironmentVariable(EnvVars.EvalScoreFallbackJudgeProvider);
            if (string.Equals(envProvider, "azure-openai", StringComparison.OrdinalIgnoreCase))
            {
                provider = JudgeProvider.AzureOpenAi;
            }
            else if (string.Equals(envProvider, "github-copilot", StringComparison.OrdinalIgnoreCase))
            {
                provider = JudgeProvider.GitHubCopilot;
            }
            else
            {
                provider = JudgeProvider.GitHubCopilot;
            }
        }

        return JudgeFactory.Create(provider, workIqClient, tenantId);
    }
}
