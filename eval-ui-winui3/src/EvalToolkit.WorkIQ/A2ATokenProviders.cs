using System.Diagnostics;
using System.Text;
using EvalToolkit.Core;

namespace EvalToolkit.WorkIQ;

/// <summary>Access-token provider for WorkIQ A2A calls.</summary>
public interface IA2ATokenProvider
{
    Task<string> GetTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}

/// <summary>No-auth placeholder used when configuration is intentionally absent.</summary>
public sealed class NoopA2ATokenProvider : IA2ATokenProvider
{
    public Task<string> GetTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(string.Empty);
    }
}

/// <summary>Static bearer token provider for <c>WORK_IQ_A2A_ACCESS_TOKEN</c>.</summary>
public sealed class StaticTokenA2ATokenProvider : IA2ATokenProvider
{
    private readonly string _accessToken;

    public StaticTokenA2ATokenProvider(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("Static A2A token must be non-empty.", nameof(accessToken));
        }
        _accessToken = accessToken;
    }

    public Task<string> GetTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_accessToken);
    }
}

/// <summary>Runs a shell command that prints an A2A bearer token to stdout.</summary>
public sealed class TokenCommandA2ATokenProvider : IA2ATokenProvider
{
    private readonly string _command;
    private string? _cachedToken;

    public TokenCommandA2ATokenProvider(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("A2A token command must be non-empty.", nameof(command));
        }
        _command = command;
    }

    public async Task<string> GetTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && !string.IsNullOrEmpty(_cachedToken))
        {
            return _cachedToken;
        }

        string token = (await RunShellCommandAsync(_command, cancellationToken).ConfigureAwait(false)).Trim();
        if (string.IsNullOrEmpty(token))
        {
            throw new WorkIQException("A2A token command returned an empty access token.");
        }
        _cachedToken = token;
        return token;
    }

    private static async Task<string> RunShellCommandAsync(string command, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                : "/bin/sh",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new WorkIQException("Failed to start A2A token command.");
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode == 0)
        {
            return stdout;
        }

        throw new WorkIQException(
            FormattableString.Invariant($"Command exited with code {process.ExitCode}: {stderr}"));
    }
}

/// <summary>
/// Placeholder for the auth-port todo. The MSAL/WAM implementation will
/// replace this class when <c>auth-port</c> lands; this port only defines
/// the provider seam and preserves selection order.
/// </summary>
public sealed class MsalA2ATokenProvider : IA2ATokenProvider
{
    public Task<string> GetTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            "MSAL A2A auth is implemented by the auth-port todo. Configure WORK_IQ_A2A_ACCESS_TOKEN or WORK_IQ_A2A_TOKEN_COMMAND for now.");
    }
}

/// <summary>Factory for the WorkIQ A2A token-provider chain.</summary>
public static class A2ATokenProviderFactory
{
    public static IA2ATokenProvider Create(
        string? accessToken,
        string? tokenCommand,
        string? authMode,
        IA2ATokenProvider? explicitProvider = null)
    {
        if (explicitProvider is not null)
        {
            return explicitProvider;
        }

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            return new StaticTokenA2ATokenProvider(accessToken.Trim());
        }

        if (!string.IsNullOrWhiteSpace(tokenCommand))
        {
            return new TokenCommandA2ATokenProvider(tokenCommand.Trim());
        }

        string normalized = NormalizeAuthMode(authMode);
        if (normalized == "msal")
        {
            return new MsalA2ATokenProvider();
        }

        return new NoopA2ATokenProvider();
    }

    public static IA2ATokenProvider CreateFromEnvironment(string? authMode = null, IA2ATokenProvider? explicitProvider = null)
    {
        return Create(
            Environment.GetEnvironmentVariable(EnvVars.WorkIqA2aAccessToken),
            EnvHelpers.GetFirstEnv(EnvVars.WorkIqA2aTokenCommand, EnvVars.EvalScoreA2aTokenCommand),
            authMode ?? EnvHelpers.GetFirstEnv(
                EnvVars.EvalScoreA2aAuthMode,
                EnvVars.WorkIqA2aAuthMode,
                EnvVars.EvalScoreA2aAuth,
                EnvVars.WorkIqA2aAuth),
            explicitProvider);
    }

    public static string NormalizeAuthMode(string? raw)
    {
        string value = raw?.Trim().ToLowerInvariant() ?? string.Empty;
        if (value.Length == 0 || value == "auto")
        {
            return "auto";
        }
        if (value is "token" or "command" or "msal")
        {
            return value;
        }
        throw new WorkIQException(
            $"Unsupported A2A auth mode \"{raw}\". Supported values are \"auto\", \"token\", \"command\", and \"msal\".");
    }
}
