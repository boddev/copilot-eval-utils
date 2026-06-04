using EvalToolkit.EvalScore;

namespace EvalToolkit.EvalScore.Tests;

public class SmokeTests
{
    [Fact]
    public void EvalScore_Assembly_Marker_Is_Present()
    {
        Assert.Equal("EvalToolkit.EvalScore", EvalScoreInfo.Name);
    }
}
