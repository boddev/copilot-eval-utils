using EvalToolkit.Core;
using EvalToolkit.EvalGen;
using EvalToolkit.EvalScore;

namespace EvalToolkit.Parity.Tests;

/// <summary>
/// Cross-runtime parity harness against the TypeScript implementation.
/// This is a smoke test only until the <c>parity-harness</c> phase A
/// todo lands the real TS-vs-C# diff runner.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void All_Engine_Assembly_Markers_Are_Present()
    {
        Assert.Equal("EvalToolkit.Core", CoreInfo.Name);
        Assert.Equal("EvalToolkit.EvalGen", EvalGenInfo.Name);
        Assert.Equal("EvalToolkit.EvalScore", EvalScoreInfo.Name);
    }
}
