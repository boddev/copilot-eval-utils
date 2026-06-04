using EvalToolkit.EvalGen.Readers;

namespace EvalToolkit.EvalGen.Tests.Readers;

public class DatasetRowTests
{
    [Fact]
    public void Set_NewKey_AppendsAtEnd()
    {
        var row = new DatasetRow();
        row.Set("a", 1);
        row.Set("b", 2);
        row.Set("c", 3);
        Assert.Equal(new[] { "a", "b", "c" }, row.Entries.Select(e => e.Key).ToArray());
    }

    [Fact]
    public void Set_ExistingKey_ReplacesValueInPlace()
    {
        var row = new DatasetRow();
        row.Set("a", 1);
        row.Set("b", 2);
        row.Set("a", 99);
        Assert.Equal(new[] { "a", "b" }, row.Entries.Select(e => e.Key).ToArray());
        Assert.Equal(99, row["a"]);
    }

    [Fact]
    public void Indexer_MissingKey_ReturnsNull()
    {
        var row = new DatasetRow();
        Assert.Null(row["nope"]);
    }

    [Fact]
    public void ContainsKey_OrdinalCaseSensitive()
    {
        var row = new DatasetRow();
        row.Set("Name", "Alice");
        Assert.True(row.ContainsKey("Name"));
        Assert.False(row.ContainsKey("name"));
    }

    [Fact]
    public void Set_NullValueIsAllowed()
    {
        var row = new DatasetRow();
        row.Set("x", null);
        Assert.Null(row["x"]);
        Assert.True(row.ContainsKey("x"));
    }
}
