using EvalToolkit.Core;
using EvalToolkit.EvalGen.LlmClients;

namespace EvalToolkit.EvalGen.Tests.LlmClients;

[Collection("EnvVarSerial")]
public sealed class LlmClientFactoryTests
{
    private sealed record Payload(string Name);

    [Fact]
    public void ResolveProvider_DefaultsToM365Copilot()
    {
        EnvScope.Without(EnvVars.EvalGenProvider, () =>
        {
            LLMProvider p = LlmClientFactory.ResolveProvider(null);
            Assert.Equal(LLMProvider.M365Copilot, p);
        });
    }

    [Fact]
    public void ResolveProvider_PrefersOptionsOverEnv()
    {
        EnvScope.Set(EnvVars.EvalGenProvider, "github-copilot", () =>
        {
            LLMProvider p = LlmClientFactory.ResolveProvider(new LlmClientOptions { Provider = LLMProvider.AzureOpenAi });
            Assert.Equal(LLMProvider.AzureOpenAi, p);
        });
    }

    [Fact]
    public void ResolveProvider_HonorsEnvVarWhenOptionsMissing()
    {
        EnvScope.Set(EnvVars.EvalGenProvider, "workiq-a2a", () =>
        {
            LLMProvider p = LlmClientFactory.ResolveProvider(null);
            Assert.Equal(LLMProvider.WorkIqA2a, p);
        });
    }

    [Fact]
    public void Create_GitHubCopilot()
    {
        ILlmClient client = LlmClientFactory.Create(new LlmClientOptions { Provider = LLMProvider.GitHubCopilot });
        Assert.IsType<GitHubCopilotCliLlmClient>(client);
    }

    [Fact]
    public void Create_Command_ReadsEnvVarFallback()
    {
        EnvScope.Set(EnvVars.EvalGenLlmCommand, "echo hi", () =>
        {
            ILlmClient client = LlmClientFactory.Create(new LlmClientOptions { Provider = LLMProvider.Command });
            Assert.IsType<CommandLlmClient>(client);
        });
    }

    [Fact]
    public void Create_Command_PrefersExplicitOption()
    {
        EnvScope.Set(EnvVars.EvalGenLlmCommand, "echo env", () =>
        {
            ILlmClient client = LlmClientFactory.Create(new LlmClientOptions
            {
                Provider = LLMProvider.Command,
                Command = "echo explicit",
            });
            Assert.IsType<CommandLlmClient>(client);
        });
    }

    [Fact]
    public void Create_Command_FailsWhenNoCommandConfigured()
    {
        EnvScope.Without(EnvVars.EvalGenLlmCommand, () =>
        {
            Assert.Throws<InvalidOperationException>(() =>
                LlmClientFactory.Create(new LlmClientOptions { Provider = LLMProvider.Command }));
        });
    }

    [Fact]
    public void Create_AzureOpenAI_RequiresEndpointAndKey()
    {
        EnvScope.Without(
            EnvVars.EvalGenAzureOpenAiEndpoint,
            EnvVars.EvalGenAzureOpenAiKey,
            EnvVars.AzureOpenAiApiKey,
            () =>
            {
                Assert.Throws<InvalidOperationException>(() =>
                    LlmClientFactory.Create(new LlmClientOptions { Provider = LLMProvider.AzureOpenAi }));
            });
    }

    [Fact]
    public void Create_AzureOpenAI_FromOptions()
    {
        ILlmClient client = LlmClientFactory.Create(new LlmClientOptions
        {
            Provider = LLMProvider.AzureOpenAi,
            Endpoint = "https://example.openai.azure.com",
            ApiKey = "fake",
            Model = "gpt-4o",
        });
        Assert.IsType<AzureOpenAILlmClient>(client);
    }
}
