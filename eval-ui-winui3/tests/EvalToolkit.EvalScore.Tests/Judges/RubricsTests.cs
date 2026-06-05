using EvalToolkit.EvalScore.Judges;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Tests.Judges;

public class RubricsTests
{
    [Fact]
    public void RubricVersion_IsExpected()
    {
        Assert.Equal("evalscore-m365-rubrics-v1", Rubrics.RubricVersion);
    }

    [Fact]
    public void Map_ContainsAllFiveExpectedKeys()
    {
        Assert.Contains(EvaluatorName.Relevance, Rubrics.Map.Keys);
        Assert.Contains(EvaluatorName.Coherence, Rubrics.Map.Keys);
        Assert.Contains(EvaluatorName.Groundedness, Rubrics.Map.Keys);
        Assert.Contains(EvaluatorName.Similarity, Rubrics.Map.Keys);
        Assert.Contains(EvaluatorName.SemanticSimilarity, Rubrics.Map.Keys);
        Assert.Equal(5, Rubrics.Map.Count);
    }

    [Fact]
    public void Similarity_AndSemanticSimilarity_ShareIdenticalText()
    {
        Assert.Equal(Rubrics.Map[EvaluatorName.Similarity], Rubrics.Map[EvaluatorName.SemanticSimilarity]);
    }

    [Fact]
    public void RubricTexts_AreNotEmpty()
    {
        foreach (var text in Rubrics.Map.Values)
        {
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
    }
}
