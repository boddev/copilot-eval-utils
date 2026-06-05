namespace EvalToolkit.EvalScore.Models;

/// <summary>
/// Judge providers supported by EvalScore. Mirrors the TS
/// <c>JudgeProvider</c> union in <c>eval-score/node/src/types.ts</c>.
/// </summary>
public enum JudgeProvider
{
    /// <summary>Default — routes through the WorkIQ client (MCP or A2A).</summary>
    WorkIq,

    /// <summary>Local <c>copilot</c> CLI (NOT the same as EvalGen's <c>gh copilot</c>).</summary>
    GitHubCopilot,

    /// <summary>Azure OpenAI chat completions (raw HttpClient, temperature 0).</summary>
    AzureOpenAi,
}

/// <summary>Wire-string mapping for <see cref="JudgeProvider"/>.</summary>
public static class JudgeProviders
{
    public static string ToWireString(this JudgeProvider provider) => provider switch
    {
        JudgeProvider.WorkIq => "workiq",
        JudgeProvider.GitHubCopilot => "github-copilot",
        JudgeProvider.AzureOpenAi => "azure-openai",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    public static JudgeProvider FromWireString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant() switch
        {
            "workiq" => JudgeProvider.WorkIq,
            "github-copilot" => JudgeProvider.GitHubCopilot,
            "azure-openai" => JudgeProvider.AzureOpenAi,
            _ => throw new NotSupportedException($"Unknown judge provider: '{value}'"),
        };
    }
}

/// <summary>
/// Provider tag for <see cref="MetricResult"/>. Mirrors the TS
/// <c>JudgeProvider | 'deterministic'</c> union — superset of
/// <see cref="JudgeProvider"/> with <see cref="Deterministic"/>
/// added for non-LLM checks (assertions, exact match, etc.).
/// </summary>
public enum MetricProvider
{
    WorkIq,
    GitHubCopilot,
    AzureOpenAi,
    Deterministic,
}

/// <summary>Wire-string mapping for <see cref="MetricProvider"/>.</summary>
public static class MetricProviders
{
    public static string ToWireString(this MetricProvider provider) => provider switch
    {
        MetricProvider.WorkIq => "workiq",
        MetricProvider.GitHubCopilot => "github-copilot",
        MetricProvider.AzureOpenAi => "azure-openai",
        MetricProvider.Deterministic => "deterministic",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    public static MetricProvider FromWireString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant() switch
        {
            "workiq" => MetricProvider.WorkIq,
            "github-copilot" => MetricProvider.GitHubCopilot,
            "azure-openai" => MetricProvider.AzureOpenAi,
            "deterministic" => MetricProvider.Deterministic,
            _ => throw new NotSupportedException($"Unknown metric provider: '{value}'"),
        };
    }

    /// <summary>Widen a <see cref="JudgeProvider"/> into the metric provider space.</summary>
    public static MetricProvider FromJudge(JudgeProvider judge) => judge switch
    {
        JudgeProvider.WorkIq => MetricProvider.WorkIq,
        JudgeProvider.GitHubCopilot => MetricProvider.GitHubCopilot,
        JudgeProvider.AzureOpenAi => MetricProvider.AzureOpenAi,
        _ => throw new ArgumentOutOfRangeException(nameof(judge), judge, null),
    };
}
