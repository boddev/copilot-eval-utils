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
    [InlineData("-42")]               // Negative parsed value -> default.
    public void ParsePositiveIntEnv_UsesDefaultOnNonPositiveOrInvalid(string raw)
    {
        // Matches TS parsePositiveIntEnv: zero / negative / NaN -> default.
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
    public void ParsePositiveIntEnv_OverflowsInt32_FallsBackToDefault_IntentionalDivergence()
    {
        // **Intentional divergence from TS parsePositiveIntEnv.**
        // JS Number is double-backed, so TS returns the full
        // 99999999999. The C# port clamps to Int32 because every
        // consumer of this helper today is an int-typed timeout /
        // attempt count, where any value past Int32 is operationally
        // pathological (>24 days of milliseconds). The parity harness
        // EXCLUDES overflow inputs from its env-comparison vectors so
        // this divergence is well-bounded. If a future consumer needs
        // 64-bit support, add a sibling `ParsePositiveLongEnv` rather
        // than widening this one.
        string name = $"_EVALTK_UT_OVF_{Guid.NewGuid():N}";
        try
        {
            Environment.SetEnvironmentVariable(name, "99999999999");
            Assert.Equal(42, EnvHelpers.ParsePositiveIntEnv(name, 42));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Theory]
    [InlineData("30s", 30)]            // Leading digits, trailing junk: parseInt-style.
    [InlineData("1e3", 1)]             // parseInt(_,10) stops at 'e'.
    [InlineData("  42  ", 42)]         // Leading whitespace tolerated.
    [InlineData("+42", 42)]            // Explicit positive sign.
    [InlineData("042", 42)]            // Leading zero, base 10 (no octal).
    [InlineData("7 ms", 7)]            // Stops at first non-digit.
    public void ParsePositiveIntEnv_MatchesJsParseIntLeadingDigits(string raw, int expected)
    {
        // Real-world reason: a user writes EVALSCORE_WORKIQ_TIMEOUT_MS=30000ms
        // expecting "30 seconds". On the TS side this becomes 30000. The
        // C# side must agree, or "the same env produces different behavior"
        // between the two implementations.
        string name = $"_EVALTK_UT_JS_{Guid.NewGuid():N}";
        try
        {
            Environment.SetEnvironmentVariable(name, raw);
            Assert.Equal(expected, EnvHelpers.ParsePositiveIntEnv(name, 99));
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
    public void GetFirstEnv_ReturnsEmptyStringWhenAllUnset()
    {
        // Matches TS getFirstEnv: returns '' when nothing is set, NOT
        // null. Lets callers chain via `||` (TS) / `string.IsNullOrEmpty`
        // (C#) without a runtime divergence between the two ports.
        string a = $"_EVALTK_UT_UA_{Guid.NewGuid():N}";
        string b = $"_EVALTK_UT_UB_{Guid.NewGuid():N}";
        Assert.Equal(string.Empty, EnvHelpers.GetFirstEnv(a, b));
    }

    [Fact]
    public void GetFirstEnv_TreatsWhitespaceOnlyValuesAsUnset()
    {
        // Matches TS getFirstEnv: process.env[name]?.trim() before the
        // truthy check. A user setting EVALSCORE_A2A_CLIENT_ID="   "
        // should fall through to the next alias, not pretend it's set.
        string a = $"_EVALTK_UT_WS_A_{Guid.NewGuid():N}";
        string b = $"_EVALTK_UT_WS_B_{Guid.NewGuid():N}";
        try
        {
            Environment.SetEnvironmentVariable(a, "   ");
            Environment.SetEnvironmentVariable(b, "real-value");
            Assert.Equal("real-value", EnvHelpers.GetFirstEnv(a, b));
        }
        finally
        {
            Environment.SetEnvironmentVariable(a, null);
            Environment.SetEnvironmentVariable(b, null);
        }
    }

    [Fact]
    public void GetFirstEnv_TrimsReturnedValue()
    {
        // Matches TS getFirstEnv contract: returned value is trimmed,
        // so e.g. EVALSCORE_A2A_TENANT_ID=" 1234 " doesn't carry leading
        // / trailing whitespace into MSAL config (which would silently
        // fail authentication).
        string a = $"_EVALTK_UT_TR_{Guid.NewGuid():N}";
        try
        {
            Environment.SetEnvironmentVariable(a, "  contoso-tenant-id  ");
            Assert.Equal("contoso-tenant-id", EnvHelpers.GetFirstEnv(a));
        }
        finally
        {
            Environment.SetEnvironmentVariable(a, null);
        }
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
        Assert.Equal("EVALGEN_LLM_MAX_ATTEMPTS", EnvVars.EvalGenLlmMaxAttempts);
        Assert.Equal("EVALGEN_LLM_BACKOFF_MS", EnvVars.EvalGenLlmBackoffMs);
        Assert.Equal("EVALGEN_PPTX_INCLUDE_MASTER", EnvVars.EvalGenPptxIncludeMaster);
        Assert.Equal("WORK_IQ_A2A_ACCESS_TOKEN", EnvVars.WorkIqA2aAccessToken);
        Assert.Equal("WORK_IQ_A2A_TOKEN_COMMAND", EnvVars.WorkIqA2aTokenCommand);
        Assert.Equal("EVALTOOLKIT_WORKSPACE_DIR", EnvVars.EvalToolkitWorkspaceDir);
    }
}
