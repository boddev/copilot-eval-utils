namespace EvalToolkit.EvalScore.Tests.Judges;

/// <summary>
/// Tiny helper that sets/clears an env var for the duration of an action.
/// Tests using this MUST be in the <c>EvalScoreEnvVarSerial</c> xunit
/// collection to avoid concurrent mutation across parallel test cases.
/// </summary>
internal static class EnvScope
{
    public static void Set(string name, string? value, Action body)
    {
        string? original = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }

    public static void Without(string a, string b, Action body)
    {
        string? origA = Environment.GetEnvironmentVariable(a);
        string? origB = Environment.GetEnvironmentVariable(b);
        try
        {
            Environment.SetEnvironmentVariable(a, null);
            Environment.SetEnvironmentVariable(b, null);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(a, origA);
            Environment.SetEnvironmentVariable(b, origB);
        }
    }

    public static void WithoutAll(IEnumerable<string> names, Action body)
    {
        var saved = new Dictionary<string, string?>();
        try
        {
            foreach (string n in names)
            {
                saved[n] = Environment.GetEnvironmentVariable(n);
                Environment.SetEnvironmentVariable(n, null);
            }
            body();
        }
        finally
        {
            foreach (var kvp in saved)
            {
                Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
            }
        }
    }

    public static void With(IDictionary<string, string?> values, Action body)
    {
        var saved = new Dictionary<string, string?>();
        try
        {
            foreach (var kvp in values)
            {
                saved[kvp.Key] = Environment.GetEnvironmentVariable(kvp.Key);
                Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
            }
            body();
        }
        finally
        {
            foreach (var kvp in saved)
            {
                Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
            }
        }
    }
}

#pragma warning disable CA1711
[CollectionDefinition("EvalScoreEnvVarSerial", DisableParallelization = true)]
public class EvalScoreEnvVarSerialCollection
{
}
#pragma warning restore CA1711
