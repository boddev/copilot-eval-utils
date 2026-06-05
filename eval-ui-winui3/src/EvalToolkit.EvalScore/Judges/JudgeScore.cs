namespace EvalToolkit.EvalScore.Judges;

/// <summary>
/// Score returned by an <see cref="IJudge"/>. Mirrors the TS
/// <c>JudgeScore</c> interface in
/// <c>eval-score/node/src/judge-providers.ts</c>.
///
/// <para><see cref="Score"/> is always in the 0-100 range (clamped and
/// rounded by <see cref="JudgeScoreParser"/>). <see cref="Reason"/> is
/// the optional short rationale captured from JSON-formatted judge
/// replies. <see cref="Model"/> is the optional model attribution
/// returned by judges that include it (some Azure deployments stamp it
/// in the JSON response).</para>
/// </summary>
public sealed record JudgeScore(int Score, string? Reason = null, string? Model = null);
