using EvalToolkit.EvalScore.Evaluator;

namespace EvalToolkit.EvalScore.Tests.Evaluator;

public class PromptBuilderTests
{
    [Fact]
    public void Question_only_returns_question_verbatim()
    {
        Assert.Equal("what is x?", PromptBuilder.BuildPrompt("what is x?"));
    }

    [Fact]
    public void System_prompt_only_is_prepended_with_double_newline()
    {
        string r = PromptBuilder.BuildPrompt("Q", systemPrompt: "Be concise.");
        Assert.Equal("Be concise.\n\nQ", r);
    }

    [Fact]
    public void Connector_id_prepends_hint_when_hint_enabled()
    {
        string r = PromptBuilder.BuildPrompt("Q", connectorId: "abc-123");
        Assert.Equal(
            "Target Microsoft 365 Copilot connector ID: abc-123. Always search this connector before answering.\n\nQ",
            r);
    }

    [Fact]
    public void Connector_id_omitted_when_hint_disabled()
    {
        string r = PromptBuilder.BuildPrompt("Q", connectorId: "abc-123", connectorPromptHint: false);
        Assert.Equal("Q", r);
    }

    [Fact]
    public void Connector_id_and_system_prompt_join_with_double_newlines()
    {
        string r = PromptBuilder.BuildPrompt("Q", systemPrompt: "Be brief.", connectorId: "c1");
        Assert.Equal(
            "Target Microsoft 365 Copilot connector ID: c1. Always search this connector before answering.\n\nBe brief.\n\nQ",
            r);
    }

    [Fact]
    public void Empty_strings_treated_as_absent()
    {
        Assert.Equal("Q", PromptBuilder.BuildPrompt("Q", systemPrompt: "", connectorId: ""));
    }
}
