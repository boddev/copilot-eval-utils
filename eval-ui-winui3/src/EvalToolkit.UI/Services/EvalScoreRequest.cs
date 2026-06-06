using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Snapshot of wizard Step 5 inputs, ready to hand to
/// <see cref="IEvalScoreJobService.RunAsync"/>. Captured by the VM at
/// "Run scoring" time so subsequent edits to the wizard don't perturb a
/// running scoring job.
/// </summary>
public sealed record EvalScoreRequest
{
    /// <summary>Path to the <c>.evalgen.json</c> sidecar produced by EvalGen.</summary>
    public required string EvalSetPath { get; init; }

    /// <summary>Directory the report (and scored CSV) is written to. Job folder by default.</summary>
    public required string OutputDir { get; init; }

    /// <summary>Pass threshold 0..100. Default 70 mirrors Electron UI.</summary>
    public required double Threshold { get; init; }

    /// <summary>Connector hint sent to WorkIQ; passes through unchanged.</summary>
    public string? ConnectorId { get; init; }

    /// <summary>Microsoft Entra tenant id for WorkIQ.</summary>
    public string? TenantId { get; init; }

    /// <summary>Inline system prompt prepended to each user turn. Optional.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Judge provider: WorkIQ (default), AzureOpenAI, GitHubCopilot.</summary>
    public required JudgeProvider JudgeProvider { get; init; }

    /// <summary>Optional M365 agent id (enables WorkIQ A2A for response generation).</summary>
    public string? M365AgentId { get; init; }

    /// <summary>Optional WorkIQ judge agent id (enables A2A judging when paired with M365AgentId).</summary>
    public string? JudgeAgentId { get; init; }

    /// <summary>Skip the preflight EULA / connectivity check. Mirrors CLI flag.</summary>
    public bool SkipPreflight { get; init; }

    /// <summary>Per-worker delay between rows in milliseconds. Default 500 (mirrors CLI).</summary>
    public int DelayMs { get; init; } = 500;

    /// <summary>Parallel workers (clamped 1..5 by ThrottleGate). Default 1.</summary>
    public int Concurrency { get; init; } = 1;
}
