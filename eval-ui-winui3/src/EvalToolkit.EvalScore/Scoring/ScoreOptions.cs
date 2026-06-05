using EvalToolkit.EvalScore.Judges;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Scoring;

/// <summary>
/// Per-call configuration for <see cref="Scorer"/>. Mirrors the TS
/// options object passed to <c>scoreAnswers</c> in
/// <c>eval-score/node/src/scorer.ts</c>.
/// </summary>
public sealed record ScoreOptions
{
    public string? TenantId { get; init; }
    public JudgeProvider? JudgeProvider { get; init; }

    /// <summary>
    /// Fallback provider. <c>null</c> means "default" (TS picks
    /// github-copilot when primary is workiq and env doesn't disable
    /// the fallback). To explicitly disable the fallback, set
    /// <see cref="DisableFallbackJudge"/> to true.
    /// </summary>
    public JudgeProvider? FallbackJudgeProvider { get; init; }

    /// <summary>If true, skip the default github-copilot fallback (TS
    /// <c>'none'</c> sentinel or <c>EVALSCORE_DISABLE_GITHUB_FALLBACK=1</c>).</summary>
    public bool DisableFallbackJudge { get; init; }

    public IJudge? Judge { get; init; }
    public IJudge? FallbackJudge { get; init; }
    public string? JudgeAgentId { get; init; }

    public IReadOnlyList<EvaluatorName>? Evaluators { get; init; }

    public int? Concurrency { get; init; }
    public int? DelayMs { get; init; }
    public double? Threshold { get; init; }

    public Action<int, int>? OnProgress { get; init; }

    public Func<IReadOnlyList<EvalRow>, EvalRow, int, CancellationToken, Task>? OnRowCompleteAsync { get; init; }
}
