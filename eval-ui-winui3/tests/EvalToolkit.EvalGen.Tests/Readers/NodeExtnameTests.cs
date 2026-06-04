using EvalToolkit.EvalGen.Readers;

namespace EvalToolkit.EvalGen.Tests.Readers;

/// <summary>
/// Pins the <see cref="DatasetReader.NodeExtname"/> helper to the Node
/// <c>path.extname</c> rule (verified empirically against
/// <c>node -e "require('path').extname(...)"</c> at round 5). The
/// vectors below come straight from that probe.
/// </summary>
public class NodeExtnameTests
{
    [Theory]
    [InlineData("foo.csv", ".csv")]
    [InlineData(".csv", "")]
    [InlineData(".foo.csv", ".csv")]
    [InlineData("foo", "")]
    [InlineData("foo.", ".")]
    [InlineData(".foo", "")]
    [InlineData("a.b.c", ".c")]
    [InlineData("...", ".")]
    [InlineData("..csv", ".csv")]
    public void NodeExtname_MatchesNodeRule(string fileName, string expected)
    {
        Assert.Equal(expected, DatasetReader.NodeExtname(fileName));
    }

    [Fact]
    public void NodeExtname_FullPath_UsesFileNameOnly()
    {
        // Node's path.extname only considers the basename, never any
        // dots in directory names.
        Assert.Equal(".csv", DatasetReader.NodeExtname("a.b/c.d/foo.csv"));
        Assert.Equal(string.Empty, DatasetReader.NodeExtname("a.b/c.d/.csv"));
    }
}
