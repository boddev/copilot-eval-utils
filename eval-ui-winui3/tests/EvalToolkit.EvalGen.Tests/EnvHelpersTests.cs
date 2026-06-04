using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Tests;

/// <summary>
/// Tests for <see cref="EnvHelpers"/>. Must match the TS reference
/// behavior exactly — these helpers gate parity of every retry, every
/// timeout, every concurrency knob, and every PPTX include-master
/// decision.
/// </summary>
public class EnvHelpersTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("YES", true)]
    [InlineData("on", true)]
    [InlineData("ON", true)]
    [InlineData("  true  ", true)]
    public void ParseBoolEnv_RecognizesTruthyValues(string value, bool expected)
    {
        Assert.Equal(expected, EnvHelpers.ParseBoolEnv(value));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("anything")]
    [InlineData("2")]
    [InlineData(null)]
    public void ParseBoolEnv_TreatsEverythingElseAsFalse(string? value)
    {
        Assert.False(EnvHelpers.ParseBoolEnv(value));
    }

    // ParsePositiveIntEnv / GetBoolEnv / GetFirstEnv hit Environment
    // directly; we use uniquely-prefixed names to avoid colliding with
    // anything the test runner might already have set.

    [Fact]
    public void ParsePositiveIntEnv_UsesDefaultWhenUnset()
    {
        string name = $"_EVALTK_UT_UNSET_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, null);
        Assert.Equal(42, EnvHelpers.ParsePositiveIntEnv(name, 42));
    }

    [Fact]
    public void ParsePositiveIntEnv_ParsesPositiveValue()
    {
        string name = $"_EVALTK_UT_POS_{Guid.NewGuid():N}";
        try
        {
            Environment.SetEnvironmentVariable(name, "7000");
            Assert.Equal(7000, EnvHelpers.ParsePositiveIntEnv(name, 42));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    [InlineData("")]
    [InlineData("   ")]
    public void ParsePositiveIntEnv_UsesDefaultOnNonPositiveOrInvalid(string raw)
    {
        // Matches TS parsePositiveIntEnv: zero / negative / NaN -> default.
        // This is important for EVALSCORE_WORKIQ_MAX_ATTEMPTS: a user
        // setting it to 0 should NOT silently disable retries.
        string name = $"_EVALTK_UT_INV_{Guid.NewGuid():N}";
        try
        {
            Environment.SetEnvironmentVariable(name, raw);
            Assert.Equal(42, EnvHelpers.ParsePositiveIntEnv(name, 42));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void GetFirstEnv_ReturnsFirstNonEmpty()
    {
        string a = $"_EVALTK_UT_A_{Guid.NewGuid():N}";
        string b = $"_EVALTK_UT_B_{Guid.NewGuid():N}";
        string c = $"_EVALTK_UT_C_{Guid.NewGuid():N}";
        try
        {
            Environment.SetEnvironmentVariable(a, "");
            Environment.SetEnvironmentVariable(b, "found-it");
            Environment.SetEnvironmentVariable(c, "also-set");
            Assert.Equal("found-it", EnvHelpers.GetFirstEnv(a, b, c));
        }
        finally
        {
            Environment.SetEnvironmentVariable(a, null);
            Environment.SetEnvironmentVariable(b, null);
            Environment.SetEnvironmentVariable(c, null);
        }
    }

    [Fact]
    public void GetFirstEnv_ReturnsNullWhenAllUnset()
    {
        string a = $"_EVALTK_UT_UA_{Guid.NewGuid():N}";
        string b = $"_EVALTK_UT_UB_{Guid.NewGuid():N}";
        Assert.Null(EnvHelpers.GetFirstEnv(a, b));
    }

    [Fact]
    public void EnvVars_HasAllExpectedConstants()
    {
        // Smoke check: make sure the catalog actually exposes the
        // critical names that downstream code looks up. If somebody
        // renames one of these, builds across the engines will break
        // loudly rather than silently bypassing an env var.
        Assert.Equal("EVALSCORE_WORKIQ_MAX_ATTEMPTS", EnvVars.EvalScoreWorkIqMaxAttempts);
        Assert.Equal("EVALSCORE_WORKIQ_BACKOFF_MS", EnvVars.EvalScoreWorkIqBackoffMs);
        Assert.Equal("EVALSCORE_WORKIQ_BACKOFF_MAX_MS", EnvVars.EvalScoreWorkIqBackoffMaxMs);
        Assert.Equal("EVALSCORE_MAX_CONCURRENCY", EnvVars.EvalScoreMaxConcurrency);
        Assert.Equal("EVALGEN_PPTX_INCLUDE_MASTER", EnvVars.EvalGenPptxIncludeMaster);
        Assert.Equal("WORK_IQ_A2A_ACCESS_TOKEN", EnvVars.WorkIqA2aAccessToken);
        Assert.Equal("WORK_IQ_A2A_TOKEN_COMMAND", EnvVars.WorkIqA2aTokenCommand);
        Assert.Equal("EVALTOOLKIT_WORKSPACE_DIR", EnvVars.EvalToolkitWorkspaceDir);
    }
}
