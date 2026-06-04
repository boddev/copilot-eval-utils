namespace EvalToolkit.Core;

/// <summary>
/// LLM providers supported by EvalGen. Mirrors the TS <c>LLMProvider</c>
/// union in <c>eval-gen/src/types.ts</c>. The default provider is
/// <see cref="M365Copilot"/> which routes through WorkIQ MCP.
///
/// All six values must be preserved across the TS / C# boundary —
/// reviewers flagged that an earlier draft missed <c>m365-copilot-api</c>.
/// </summary>
public enum LLMProvider
{
    /// <summary>Default — routes through the local <c>workiq</c> CLI / MCP server.</summary>
    M365Copilot,

    /// <summary>Direct Microsoft Graph beta Chat API.</summary>
    M365CopilotApi,

    /// <summary>WorkIQ A2A HTTP API (MSAL / static token / token-command auth).</summary>
    WorkIqA2a,

    /// <summary>Azure OpenAI chat completions (raw HttpClient, api-version 2024-10-21, temperature 0.7).</summary>
    AzureOpenAi,

    /// <summary>Local <c>gh copilot</c> CLI.</summary>
    GitHubCopilot,

    /// <summary>Arbitrary user-supplied command that reads JSON from stdin and prints JSON to stdout.</summary>
    Command,
}

/// <summary>String round-trip for <see cref="LLMProvider"/> matching the TS wire strings.</summary>
public static class LLMProviders
{
    public static string ToWireString(this LLMProvider provider) => provider switch
    {
        LLMProvider.M365Copilot => "m365-copilot",
        LLMProvider.M365CopilotApi => "m365-copilot-api",
        LLMProvider.WorkIqA2a => "workiq-a2a",
        LLMProvider.AzureOpenAi => "azure-openai",
        LLMProvider.GitHubCopilot => "github-copilot",
        LLMProvider.Command => "command",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    public static LLMProvider FromWireString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant() switch
        {
            "m365-copilot" => LLMProvider.M365Copilot,
            "m365-copilot-api" => LLMProvider.M365CopilotApi,
            "workiq-a2a" => LLMProvider.WorkIqA2a,
            "azure-openai" => LLMProvider.AzureOpenAi,
            "github-copilot" => LLMProvider.GitHubCopilot,
            "command" => LLMProvider.Command,
            _ => throw new NotSupportedException($"Unknown LLM provider: '{value}'"),
        };
    }
}

/// <summary>
/// Question categories for generated eval items. Matches the TS
/// <c>QuestionCategory</c> union exactly.
/// </summary>
public enum QuestionCategory
{
    SingleRecordLookup,
    AttributeRetrieval,
    FilteredFind,
    Temporal,
    Comparison,
    EdgeCase,
}

public static class QuestionCategories
{
    public static string ToWireString(this QuestionCategory category) => category switch
    {
        QuestionCategory.SingleRecordLookup => "single_record_lookup",
        QuestionCategory.AttributeRetrieval => "attribute_retrieval",
        QuestionCategory.FilteredFind => "filtered_find",
        QuestionCategory.Temporal => "temporal",
        QuestionCategory.Comparison => "comparison",
        QuestionCategory.EdgeCase => "edge_case",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    public static QuestionCategory FromWireString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant() switch
        {
            "single_record_lookup" => QuestionCategory.SingleRecordLookup,
            "attribute_retrieval" => QuestionCategory.AttributeRetrieval,
            "filtered_find" => QuestionCategory.FilteredFind,
            "temporal" => QuestionCategory.Temporal,
            "comparison" => QuestionCategory.Comparison,
            "edge_case" => QuestionCategory.EdgeCase,
            _ => throw new NotSupportedException($"Unknown question category: '{value}'"),
        };
    }

    /// <summary>
    /// Default category distribution targets. Matches
    /// <c>DEFAULT_CATEGORY_WEIGHTS</c> in <c>eval-gen/src/types.ts</c>.
    /// </summary>
    public static IReadOnlyDictionary<QuestionCategory, double> DefaultWeights { get; } =
        new Dictionary<QuestionCategory, double>
        {
            { QuestionCategory.SingleRecordLookup, 0.30 },
            { QuestionCategory.AttributeRetrieval, 0.20 },
            { QuestionCategory.FilteredFind, 0.20 },
            { QuestionCategory.Temporal, 0.10 },
            { QuestionCategory.Comparison, 0.10 },
            { QuestionCategory.EdgeCase, 0.10 },
        };
}

/// <summary>Difficulty banding for a generated question.</summary>
public enum Difficulty
{
    Easy,
    Medium,
    Hard,
}

public static class Difficulties
{
    public static string ToWireString(this Difficulty d) => d switch
    {
        Difficulty.Easy => "easy",
        Difficulty.Medium => "medium",
        Difficulty.Hard => "hard",
        _ => throw new ArgumentOutOfRangeException(nameof(d), d, null),
    };

    public static Difficulty FromWireString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant() switch
        {
            "easy" => Difficulty.Easy,
            "medium" => Difficulty.Medium,
            "hard" => Difficulty.Hard,
            _ => throw new NotSupportedException($"Unknown difficulty: '{value}'"),
        };
    }
}

/// <summary>
/// How confident the grounder is that the generated question is supported
/// by the source dataset. Used for filtering / weighting downstream.
/// </summary>
public enum GroundingConfidence
{
    High,
    Medium,
    Low,
}

public static class GroundingConfidences
{
    public static string ToWireString(this GroundingConfidence c) => c switch
    {
        GroundingConfidence.High => "high",
        GroundingConfidence.Medium => "medium",
        GroundingConfidence.Low => "low",
        _ => throw new ArgumentOutOfRangeException(nameof(c), c, null),
    };

    public static GroundingConfidence FromWireString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant() switch
        {
            "high" => GroundingConfidence.High,
            "medium" => GroundingConfidence.Medium,
            "low" => GroundingConfidence.Low,
            _ => throw new NotSupportedException($"Unknown grounding confidence: '{value}'"),
        };
    }
}
