using EvalToolkit.Core;

namespace EvalToolkit.WorkIQ;

/// <summary>
/// MSAL token-provider configuration. Mirrors
/// <c>MsalA2ATokenProviderConfig</c> in
/// <c>eval-score/node/src/workiq-client.ts</c> (around line 398).
///
/// Use <see cref="FromEnvironment(string?)"/> to build from the same
/// env-var precedence the Node tool uses; use
/// <see cref="GetMissingFields"/> to enumerate fields the operator
/// still needs to set (drives the validation error message in
/// <see cref="A2AWorkIQClient"/>).
/// </summary>
public sealed record MsalA2ATokenProviderConfig(
    string ClientId,
    string TenantId,
    IReadOnlyList<string> Scopes,
    string CachePath,
    bool AllowDeviceCode)
{
    /// <summary>
    /// Default path for the LEGACY plaintext-JSON cache produced by the
    /// Node tool (<c>~/.evalscore/msal-a2a-cache.json</c>). The WinUI
    /// port stores its own MSAL cache via DPAPI (see
    /// <see cref="MsalA2ATokenCachePaths.DefaultDpapiCachePath"/>) and
    /// imports this file on first run only — see
    /// <see cref="MsalA2ATokenProvider"/> for the import semantics.
    /// </summary>
    public static string DefaultLegacyCachePath()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".evalscore", "msal-a2a-cache.json");
    }

    /// <summary>
    /// Build a config from environment variables, mirroring the
    /// TS <c>getMsalConfig()</c> precedence chain exactly.
    /// </summary>
    /// <param name="tenantIdOverride">
    /// Optional CLI <c>--tenant-id</c> override; takes precedence over
    /// every env var (matches TS).
    /// </param>
    public static MsalA2ATokenProviderConfig FromEnvironment(string? tenantIdOverride = null)
    {
        string clientId = EnvHelpers.GetFirstEnv(
            EnvVars.EvalScoreA2aClientId,
            EnvVars.WorkIqA2aClientId);

        string tenantId = !string.IsNullOrWhiteSpace(tenantIdOverride)
            ? tenantIdOverride.Trim()
            : EnvHelpers.GetFirstEnv(
                EnvVars.EvalScoreA2aTenantId,
                EnvVars.WorkIqA2aTenantId,
                EnvVars.EvalScoreTenantId,
                EnvVars.TenantId);

        string scopesRaw = EnvHelpers.GetFirstEnv(
            EnvVars.EvalScoreA2aScopes,
            EnvVars.WorkIqA2aScopes);
        IReadOnlyList<string> scopes = ParseScopeList(scopesRaw);

        string cachePath = EnvHelpers.GetFirstEnv(
            EnvVars.EvalScoreA2aTokenCachePath,
            EnvVars.WorkIqA2aTokenCachePath);
        if (cachePath.Length == 0)
        {
            cachePath = DefaultLegacyCachePath();
        }

        // TS default: process.stderr.isTTY === true. In a Windows
        // desktop process there is normally no console at all, so
        // Console.IsErrorRedirected is the closest match: true when
        // there's a non-TTY (file/pipe) or no console at all. We invert
        // to mean "interactive console available". WinUI shell will
        // typically override this to false (broker preferred).
        bool defaultAllowDeviceCode = false;
        try
        {
            defaultAllowDeviceCode = !Console.IsErrorRedirected;
        }
        catch (System.IO.IOException)
        {
            // Some packaged desktop hosts throw on Console.IsErrorRedirected
            // when there's no console attached at all — treat that as
            // "no TTY".
            defaultAllowDeviceCode = false;
        }
        bool allowDeviceCode = EnvHelpers.GetBoolEnv(
            EnvVars.EvalScoreA2aAllowDeviceCode,
            defaultAllowDeviceCode);

        return new MsalA2ATokenProviderConfig(
            ClientId: clientId,
            TenantId: tenantId,
            Scopes: scopes,
            CachePath: cachePath,
            AllowDeviceCode: allowDeviceCode);
    }

    /// <summary>
    /// Enumerate the operator-facing field names the config is
    /// missing, in the same order TS lists them so the error message
    /// is byte-equivalent.
    /// </summary>
    public IReadOnlyList<string> GetMissingFields()
    {
        var missing = new List<string>(3);
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            missing.Add("client ID");
        }
        if (string.IsNullOrWhiteSpace(TenantId))
        {
            missing.Add("tenant ID");
        }
        if (Scopes.Count == 0)
        {
            missing.Add("scopes");
        }
        return missing;
    }

    /// <summary>
    /// Split a scope env value on whitespace OR comma, dropping empties.
    /// Mirrors TS <c>parseScopeList</c> (<c>/[\s,]+/</c> split).
    /// </summary>
    public static IReadOnlyList<string> ParseScopeList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        string[] parts = raw.Split(
            s_scopeSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return Array.Empty<string>();
        }
        return parts;
    }

    private static readonly char[] s_scopeSeparators = { ' ', '\t', '\r', '\n', '\f', '\v', ',' };
}

/// <summary>
/// Canonical paths for the MSAL token cache the WinUI port uses.
/// Kept out of <see cref="MsalA2ATokenProviderConfig"/> so tests can
/// override the directory without rebuilding a config record.
/// </summary>
public static class MsalA2ATokenCachePaths
{
    /// <summary>
    /// DPAPI-protected cache filename produced by
    /// <c>Microsoft.Identity.Client.Extensions.Msal</c>. Stored under
    /// <c>%LOCALAPPDATA%\EvalToolkit</c> by default.
    /// </summary>
    public const string DefaultCacheFileName = "msal-a2a-cache.bin";

    /// <summary>
    /// Default directory: <c>%LOCALAPPDATA%\EvalToolkit</c> on Windows;
    /// <c>$XDG_DATA_HOME/EvalToolkit</c> or <c>~/.local/share/EvalToolkit</c>
    /// on other platforms. Honors the <c>LOCALAPPDATA</c> env var when
    /// present so packaged-app redirection just works.
    /// </summary>
    public static string DefaultCacheDirectory()
    {
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            baseDir = Path.Combine(home, ".local", "share");
        }
        return Path.Combine(baseDir, "EvalToolkit");
    }

    /// <summary>Default full path to the DPAPI cache file.</summary>
    public static string DefaultDpapiCachePath()
    {
        return Path.Combine(DefaultCacheDirectory(), DefaultCacheFileName);
    }
}
