using System.Text.Json.Serialization;

namespace EvalToolkit.EvalGen.Models;

/// <summary>
/// Mutable wire-shaped POCO for an LLM-returned question intent. The LLM
/// returns snake_case JSON; deserialization populates this DTO, which the
/// orchestrator then converts to an immutable <see cref="Core.QuestionIntent"/>.
/// </summary>
public sealed class QuestionIntentDto
{
    [JsonPropertyName("intent")]
    public string Intent { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("difficulty")]
    public string Difficulty { get; set; } = string.Empty;

    [JsonPropertyName("target_fields")]
    public IList<string> TargetFields { get; set; } = new List<string>();

    [JsonPropertyName("target_row_references")]
    public IList<string> TargetRowReferences { get; set; } = new List<string>();

    [JsonPropertyName("assigned_primary_row")]
    public string? AssignedPrimaryRow { get; set; }
}

/// <summary>
/// Mutable wire-shaped POCO for an LLM-drafted question. Deserialized from
/// the LLM response, then converted to an immutable
/// <see cref="Core.DraftedQuestion"/> after the orchestrator fills in
/// <c>ReferencedFacts</c>, <c>ReferencedRows</c>, and
/// <c>AssignedPrimaryRow</c>.
/// </summary>
public sealed class DraftedQuestionDto
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("difficulty")]
    public string Difficulty { get; set; } = string.Empty;

    [JsonPropertyName("expected_answer")]
    public string ExpectedAnswer { get; set; } = string.Empty;

    [JsonPropertyName("supporting_facts")]
    public IList<string> SupportingFacts { get; set; } = new List<string>();

    [JsonPropertyName("source_location")]
    public string SourceLocation { get; set; } = string.Empty;

    [JsonPropertyName("supporting_fact_ids")]
    public IList<string>? SupportingFactIds { get; set; }
}

/// <summary>LLM-returned wrapper for a batch of intents.</summary>
public sealed class IntentsResponse
{
    [JsonPropertyName("intents")]
    public IList<QuestionIntentDto>? Intents { get; set; }
}

/// <summary>LLM-returned wrapper for a batch of drafted questions.</summary>
public sealed class QuestionsResponse
{
    [JsonPropertyName("questions")]
    public IList<DraftedQuestionDto>? Questions { get; set; }
}
