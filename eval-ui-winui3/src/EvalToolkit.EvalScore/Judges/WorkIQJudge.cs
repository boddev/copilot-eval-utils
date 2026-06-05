using EvalToolkit.EvalScore.Models;
using EvalToolkit.WorkIQ;

namespace EvalToolkit.EvalScore.Judges;

/// <summary>
/// WorkIQ-based judge (default provider). Ports TS <c>WorkIQJudge</c>
/// from <c>eval-score/node/src/judge-providers.ts</c>.
///
/// <para>Routing rule (matches TS):</para>
/// <list type="bullet">
///   <item>When <see cref="AgentId"/> is set, route through
///         <see cref="IWorkIQClient.AskWithMetadataAsync"/> with both
///         tenantId and agentId so the A2A client can target the
///         dedicated judge agent.</item>
///   <item>Otherwise fall back to the simple
///         <see cref="IWorkIQClient.AskAsync"/> path, which is what the
///         MCP/CLI client implements.</item>
/// </list>
///
/// <para>The TS <c>this.agentId &amp;&amp; this.client.askWithMetadata</c>
/// duck-type check collapses in C# because the interface always
/// includes <c>AskWithMetadataAsync</c>. Empty-string agentId still
/// routes to <c>AskAsync</c> via
/// <see cref="string.IsNullOrEmpty(string?)"/> (matches TS falsy
/// behavior — per round-1 review Q4).</para>
/// </summary>
public sealed class WorkIQJudge : IJudge
{
    private readonly IWorkIQClient _client;
    private readonly string? _tenantId;

    public WorkIQJudge(IWorkIQClient client, string? tenantId = null, string? agentId = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _tenantId = tenantId;
        AgentId = agentId;
    }

    public JudgeProvider Provider => JudgeProvider.WorkIq;

    /// <summary>WorkIQ judge does not carry a model label; the underlying provider stamps it.</summary>
    public string? Model => null;

    public string? AgentId { get; }

    public async Task<JudgeScore> ScoreAsync(EvalRow row, EvaluatorName evaluator = EvaluatorName.Similarity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        string prompt = ScoringPromptBuilder.Build(row, evaluator);

        string response;
        if (!string.IsNullOrEmpty(AgentId))
        {
            WorkIQResponse meta = await _client.AskWithMetadataAsync(
                prompt,
                new WorkIQAskOptions(TenantId: _tenantId, AgentId: AgentId),
                cancellationToken).ConfigureAwait(false);
            response = meta.Text;
        }
        else
        {
            response = await _client.AskAsync(prompt, _tenantId, cancellationToken).ConfigureAwait(false);
        }

        return JudgeScoreParser.Parse(response);
    }
}
