using EvalToolkit.EvalScore.Judges;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Tests.Judges;

public class ScoringPromptBuilderTests
{
    private static EvalRow Row(string? context = null, string sourceLocation = "src", string actual = "answer")
        => new()
        {
            Prompt = "What is X?",
            ExpectedAnswer = "X is Y.",
            SourceLocation = sourceLocation,
            ActualAnswer = actual,
            Context = context,
        };

    [Fact]
    public void SemanticSimilarity_NormalizesLabelToSimilarity()
    {
        string prompt = ScoringPromptBuilder.Build(Row(), EvaluatorName.SemanticSimilarity);
        Assert.StartsWith("Evaluate the response using the Similarity rubric.", prompt);
        Assert.Contains("semantic alignment", prompt);
    }

    [Fact]
    public void Similarity_UsesSimilarityLabelAndRubric()
    {
        string prompt = ScoringPromptBuilder.Build(Row(), EvaluatorName.Similarity);
        Assert.StartsWith("Evaluate the response using the Similarity rubric.", prompt);
        Assert.Contains("semantic alignment", prompt);
    }

    [Fact]
    public void Citations_KeepsLabelButFallsBackToSimilarityRubric()
    {
        // Per round-1 review R5: rubricless evaluators keep their own
        // label but use the Similarity rubric body.
        string prompt = ScoringPromptBuilder.Build(Row(), EvaluatorName.Citations);
        Assert.StartsWith("Evaluate the response using the Citations rubric.", prompt);
        Assert.Contains("semantic alignment", prompt);
    }

    [Fact]
    public void ExactMatch_FallsBackToSimilarityRubric()
    {
        string prompt = ScoringPromptBuilder.Build(Row(), EvaluatorName.ExactMatch);
        Assert.StartsWith("Evaluate the response using the ExactMatch rubric.", prompt);
        Assert.Contains("semantic alignment", prompt);
    }

    [Fact]
    public void PartialMatch_FallsBackToSimilarityRubric()
    {
        string prompt = ScoringPromptBuilder.Build(Row(), EvaluatorName.PartialMatch);
        Assert.StartsWith("Evaluate the response using the PartialMatch rubric.", prompt);
    }

    [Fact]
    public void EvalGenAssertions_FallsBackToSimilarityRubric()
    {
        string prompt = ScoringPromptBuilder.Build(Row(), EvaluatorName.EvalGenAssertions);
        Assert.StartsWith("Evaluate the response using the EvalGenAssertions rubric.", prompt);
    }

    [Fact]
    public void Relevance_UsesOwnRubric()
    {
        string prompt = ScoringPromptBuilder.Build(Row(), EvaluatorName.Relevance);
        Assert.Contains("directly addresses the user query", prompt);
    }

    [Fact]
    public void Coherence_UsesOwnRubric()
    {
        string prompt = ScoringPromptBuilder.Build(Row(), EvaluatorName.Coherence);
        Assert.Contains("logically organized", prompt);
    }

    [Fact]
    public void Groundedness_UsesOwnRubric()
    {
        string prompt = ScoringPromptBuilder.Build(Row(), EvaluatorName.Groundedness);
        Assert.Contains("supported by the provided context", prompt);
    }

    [Fact]
    public void Context_PreferredOverSourceLocation()
    {
        string prompt = ScoringPromptBuilder.Build(Row(context: "context-text", sourceLocation: "src-location"));
        Assert.Contains("Context / Source: context-text", prompt);
        Assert.DoesNotContain("Context / Source: src-location", prompt);
    }

    [Fact]
    public void SourceLocation_UsedWhenContextNull()
    {
        string prompt = ScoringPromptBuilder.Build(Row(context: null, sourceLocation: "src-location"));
        Assert.Contains("Context / Source: src-location", prompt);
    }

    [Fact]
    public void JsonResponseFlag_UsesJsonInstruction()
    {
        string prompt = ScoringPromptBuilder.Build(Row(), EvaluatorName.Similarity, jsonResponse: true);
        Assert.Contains("Respond with strict JSON:", prompt);
        Assert.DoesNotContain("ONLY a single number", prompt);
    }

    [Fact]
    public void DefaultResponseFlag_UsesNumericInstruction()
    {
        string prompt = ScoringPromptBuilder.Build(Row(), EvaluatorName.Similarity, jsonResponse: false);
        Assert.Contains("Respond with ONLY a single number", prompt);
        Assert.DoesNotContain("strict JSON", prompt);
    }

    [Fact]
    public void OutputUsesLineFeedNotPlatformNewline()
    {
        // TS uses Array.join('\n'); must NOT use Environment.NewLine.
        string prompt = ScoringPromptBuilder.Build(Row(), EvaluatorName.Similarity);
        Assert.Contains('\n', prompt);
        Assert.DoesNotContain("\r\n", prompt);
    }

    [Fact]
    public void PromptIncludesAllRequiredSections()
    {
        var row = Row();
        string prompt = ScoringPromptBuilder.Build(row);
        Assert.Contains($"Prompt: {row.Prompt}", prompt);
        Assert.Contains($"Expected or Ground-Truth Response: {row.ExpectedAnswer}", prompt);
        Assert.Contains($"Actual Answer: {row.ActualAnswer}", prompt);
        Assert.Contains("Use a 0 to 100 scale", prompt);
    }
}
