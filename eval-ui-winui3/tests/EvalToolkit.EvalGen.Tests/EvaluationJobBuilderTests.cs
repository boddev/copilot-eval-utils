using EvalToolkit.Core.Concurrency;

namespace EvalToolkit.EvalGen.Tests;

/// <summary>
/// Tests for <see cref="EvaluationJobBuilder.Build{TRow}"/>. Pins the
/// grouping semantics of TS <c>buildEvaluationJobs</c> in
/// <c>eval-score/node/src/evaluator.ts</c>:
///
/// <list type="bullet">
///   <item>Rows without <c>turnIndex</c> are single-row jobs in input order.</item>
///   <item>Rows with <c>turnIndex</c> group by thread key
///     (<c>threadId ?? id ?? itemIndex ?? rowIndex</c>) and sort by turnIndex.</item>
///   <item>Job order is the order of each thread's first member.</item>
/// </list>
/// </summary>
public class EvaluationJobBuilderTests
{
    private sealed record FakeRow(
        int? TurnIndex = null,
        string? ThreadId = null,
        string? ItemId = null,
        int? ItemIndex = null);

    private static IReadOnlyList<EvaluationJob<FakeRow>> Build(IReadOnlyList<FakeRow> rows) =>
        EvaluationJobBuilder.Build(rows, (r, _) => new RowKey(r.TurnIndex, r.ThreadId, r.ItemId, r.ItemIndex));

    [Fact]
    public void Build_EmptyInput_ReturnsEmpty()
    {
        var jobs = Build(Array.Empty<FakeRow>());
        Assert.Empty(jobs);
    }

    [Fact]
    public void Build_RowsWithoutTurnIndex_AreSingleRowJobs()
    {
        var rows = new[] { new FakeRow(), new FakeRow(), new FakeRow() };
        var jobs = Build(rows);
        Assert.Equal(3, jobs.Count);
        Assert.All(jobs, j => Assert.Single(j.RowIndices));
        Assert.Equal(new[] { 0 }, jobs[0].RowIndices);
        Assert.Equal(new[] { 1 }, jobs[1].RowIndices);
        Assert.Equal(new[] { 2 }, jobs[2].RowIndices);
    }

    [Fact]
    public void Build_RowsWithThreadId_GroupAndSortByTurnIndex()
    {
        var rows = new[]
        {
            new FakeRow(TurnIndex: 2, ThreadId: "T1"),
            new FakeRow(TurnIndex: 0, ThreadId: "T1"),
            new FakeRow(TurnIndex: 1, ThreadId: "T1"),
        };
        var jobs = Build(rows);
        Assert.Single(jobs);
        Assert.Equal(new[] { 1, 2, 0 }, jobs[0].RowIndices);
        Assert.Equal("T1", jobs[0].ThreadKey);
        Assert.True(jobs[0].IsMultiTurn);
    }

    [Fact]
    public void Build_MultipleThreads_PreservesFirstAppearanceOrder()
    {
        var rows = new[]
        {
            new FakeRow(TurnIndex: 0, ThreadId: "T2"),
            new FakeRow(TurnIndex: 0, ThreadId: "T1"),
            new FakeRow(TurnIndex: 1, ThreadId: "T2"),
            new FakeRow(TurnIndex: 1, ThreadId: "T1"),
        };
        var jobs = Build(rows);
        Assert.Equal(2, jobs.Count);
        Assert.Equal("T2", jobs[0].ThreadKey);
        Assert.Equal(new[] { 0, 2 }, jobs[0].RowIndices);
        Assert.Equal("T1", jobs[1].ThreadKey);
        Assert.Equal(new[] { 1, 3 }, jobs[1].RowIndices);
    }

    [Fact]
    public void Build_ThreadKeyFallsBackToItemIdThenItemIndexThenRowIndex()
    {
        var rows = new[]
        {
            // No threadId, falls back to itemId.
            new FakeRow(TurnIndex: 0, ItemId: "alpha"),
            new FakeRow(TurnIndex: 1, ItemId: "alpha"),
            // No threadId/itemId, falls back to itemIndex.
            new FakeRow(TurnIndex: 0, ItemIndex: 7),
            new FakeRow(TurnIndex: 1, ItemIndex: 7),
            // No threadId/itemId/itemIndex, falls back to row index — singleton.
            new FakeRow(TurnIndex: 0),
        };
        var jobs = Build(rows);
        Assert.Equal(3, jobs.Count);
        Assert.Equal("alpha", jobs[0].ThreadKey);
        Assert.Equal(new[] { 0, 1 }, jobs[0].RowIndices);
        Assert.Equal("7", jobs[1].ThreadKey);
        Assert.Equal(new[] { 2, 3 }, jobs[1].RowIndices);
        // The lonely row's thread key is its row index, "4".
        Assert.Equal("4", jobs[2].ThreadKey);
        Assert.Equal(new[] { 4 }, jobs[2].RowIndices);
    }

    [Fact]
    public void Build_MixesThreadedAndUnthreaded_PreservesOriginalOrder()
    {
        var rows = new[]
        {
            new FakeRow(),                                          // 0: singleton
            new FakeRow(TurnIndex: 1, ThreadId: "T"),               // 1: thread T
            new FakeRow(),                                          // 2: singleton
            new FakeRow(TurnIndex: 0, ThreadId: "T"),               // 3: thread T (sorts first)
        };
        var jobs = Build(rows);
        Assert.Equal(3, jobs.Count);
        Assert.Equal(new[] { 0 }, jobs[0].RowIndices);
        Assert.Equal(new[] { 3, 1 }, jobs[1].RowIndices);
        Assert.Equal("T", jobs[1].ThreadKey);
        Assert.Equal(new[] { 2 }, jobs[2].RowIndices);
    }
}
