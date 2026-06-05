namespace EvalToolkit.EvalGen.Tests.LlmClients;

/// <summary>
/// Tiny helper that sets/clears an env var for the duration of an action.
/// Tests using this MUST be in the <c>EnvVarSerial</c> xunit collection
/// to avoid concurrent mutation across parallel test cases.
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

    public static void Without(string name, Action body)
    {
        Set(name, null, body);
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

    public static void Without(string a, string b, string c, Action body)
    {
        string? origA = Environment.GetEnvironmentVariable(a);
        string? origB = Environment.GetEnvironmentVariable(b);
        string? origC = Environment.GetEnvironmentVariable(c);
        try
        {
            Environment.SetEnvironmentVariable(a, null);
            Environment.SetEnvironmentVariable(b, null);
            Environment.SetEnvironmentVariable(c, null);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(a, origA);
            Environment.SetEnvironmentVariable(b, origB);
            Environment.SetEnvironmentVariable(c, origC);
        }
    }

    public static async Task WithoutAsync(string a, string b, Func<Task> body)
    {
        string? origA = Environment.GetEnvironmentVariable(a);
        string? origB = Environment.GetEnvironmentVariable(b);
        try
        {
            Environment.SetEnvironmentVariable(a, null);
            Environment.SetEnvironmentVariable(b, null);
            await body().ConfigureAwait(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable(a, origA);
            Environment.SetEnvironmentVariable(b, origB);
        }
    }
}

#pragma warning disable CA1711
[CollectionDefinition("EnvVarSerial", DisableParallelization = true)]
public class EnvVarSerialCollection
{
}
#pragma warning restore CA1711
