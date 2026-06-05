using EvalToolkit.EvalGen.LlmClients;

namespace EvalToolkit.EvalGen.Tests.LlmClients;

public sealed class StructuredPromptBuilderTests
{
    [Fact]
    public void Build_ProducesExactTsFormat()
    {
        string actual = StructuredPromptBuilder.Build("Find the customer named Acme.", "Return { name: string }.");

        string expected =
            "You are a precise data analysis assistant. Always respond with valid JSON matching the requested schema.\n" +
            "\n" +
            "Return { name: string }.\n" +
            "\n" +
            "Find the customer named Acme.";

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Build_NullPrompt_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => StructuredPromptBuilder.Build(null!, "schema"));
    }

    [Fact]
    public void Build_NullSchema_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => StructuredPromptBuilder.Build("prompt", null!));
    }
}
