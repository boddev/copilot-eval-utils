using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// Builds the configured LLM client. Ports <c>createLLMClient</c> from
/// <c>eval-gen/src/llm-client.ts</c> with the same env/option precedence:
/// <c>options.Provider ?? EVALGEN_PROVIDER ?? "m365-copilot"</c>.
/// </summary>
public static class LlmClientFactory
{
    public static ILlmClient Create(LlmClientOptions? options = null)
    {
        LLMProvider provider = ResolveProvider(options);
        return provider switch
        {
            LLMProvider.M365Copilot => new WorkIQCopilotLlmClient(options),
            LLMProvider.M365CopilotApi => new Microsoft365CopilotChatLlmClient(options),
            LLMProvider.WorkIqA2a => new WorkIQA2ALlmClient(options),
            LLMProvider.AzureOpenAi => new AzureOpenAILlmClient(options),
            LLMProvider.GitHubCopilot => new GitHubCopilotCliLlmClient(options),
            LLMProvider.Command => new CommandLlmClient(
                options?.Command ?? Environment.GetEnvironmentVariable(EnvVars.EvalGenLlmCommand) ?? string.Empty),
            _ => throw new NotSupportedException($"Unsupported LLM provider: {provider}"),
        };
    }

    internal static LLMProvider ResolveProvider(LlmClientOptions? options)
    {
        if (options?.Provider is { } explicitProvider)
        {
            return explicitProvider;
        }
        string? env = Environment.GetEnvironmentVariable(EnvVars.EvalGenProvider);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return LLMProviders.FromWireString(env);
        }
        return LLMProvider.M365Copilot;
    }
}
