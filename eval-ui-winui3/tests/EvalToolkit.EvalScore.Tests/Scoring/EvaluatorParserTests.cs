using EvalToolkit.EvalScore.EvalDocument;
using EvalToolkit.EvalScore.Models;
using EvalToolkit.EvalScore.Scoring;

namespace EvalToolkit.EvalScore.Tests.Scoring;

public class EvaluatorParserTests
{
    [Fact]
    public void Null_or_whitespace_returns_default_m365_evaluators()
    {
        Assert.Same(EvalRowHelpers.DefaultM365Evaluators, EvaluatorParser.Parse(null));
        Assert.Same(EvalRowHelpers.DefaultM365Evaluators, EvaluatorParser.Parse(""));
        Assert.Same(EvalRowHelpers.DefaultM365Evaluators, EvaluatorParser.Parse("   "));
    }

    [Fact]
    public void All_returns_canonical_nine_evaluator_set()
    {
        var r = EvaluatorParser.Parse("all");
        Assert.Equal(9, r.Count);
        Assert.Equal(EvaluatorName.Similarity, r[0]);
        Assert.Equal(EvaluatorName.EvalGenAssertions, r[^1]);
    }

    [Fact]
    public void All_short_circuits_remaining_tokens()
    {
        var r = EvaluatorParser.Parse("relevance, all, citations");
        Assert.Equal(9, r.Count);
    }

    [Fact]
    public void Semantic_alias_maps_to_similarity()
    {
        var r = EvaluatorParser.Parse("semantic");
        Assert.Single(r);
        Assert.Equal(EvaluatorName.Similarity, r[0]);
    }

    [Fact]
    public void Semanticsimilarity_alias_maps_to_similarity()
    {
        var r = EvaluatorParser.Parse("semanticsimilarity");
        Assert.Single(r);
        Assert.Equal(EvaluatorName.Similarity, r[0]);
    }

    [Fact]
    public void Comma_separated_list_preserves_order()
    {
        var r = EvaluatorParser.Parse("coherence,relevance,citations");
        Assert.Equal(new[] { EvaluatorName.Coherence, EvaluatorName.Relevance, EvaluatorName.Citations }, r);
    }

    [Fact]
    public void Duplicates_are_deduplicated_keeping_first_occurrence()
    {
        var r = EvaluatorParser.Parse("relevance, coherence, relevance");
        Assert.Equal(new[] { EvaluatorName.Relevance, EvaluatorName.Coherence }, r);
    }

    [Fact]
    public void Whitespace_around_tokens_is_trimmed()
    {
        var r = EvaluatorParser.Parse("  relevance  ,  coherence  ");
        Assert.Equal(new[] { EvaluatorName.Relevance, EvaluatorName.Coherence }, r);
    }

    [Fact]
    public void Case_insensitive_token_match()
    {
        var r = EvaluatorParser.Parse("RELEVANCE,Coherence");
        Assert.Equal(new[] { EvaluatorName.Relevance, EvaluatorName.Coherence }, r);
    }

    [Fact]
    public void Unknown_token_throws_with_helpful_message()
    {
        var ex = Assert.Throws<NotSupportedException>(() => EvaluatorParser.Parse("foo"));
        Assert.Contains("foo", ex.Message);
    }
}
