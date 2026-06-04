using EvalToolkit.EvalGen;

namespace EvalToolkit.EvalGen.Tests;

/// <summary>
/// Smoke test that confirms the test project resolves the EvalGen
/// project reference and builds. Replaced with real per-component tests
/// across phase A.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void EvalGen_Assembly_Marker_Is_Present()
    {
        Assert.Equal("EvalToolkit.EvalGen", EvalGenInfo.Name);
    }
}
