namespace EvalToolkit.Core;

/// <summary>
/// Canonical names of every environment variable the EvalToolkit
/// engines consult. Centralized here so the WinUI head, the CLI shims,
/// and the tests all reference identical strings — typos in env var
/// names are otherwise silent (the variable just goes unread and a
/// default kicks in).
///
/// **All names are preserved exactly as the existing Node tools spell
/// them** so an operator can set one variable and have either app pick
/// it up. New <c>EVALTOOLKIT_*</c> aliases are additive (Section 10 of
/// the plan); they live in a separate region below.
///
/// Sources for every constant in this file:
/// <list type="bullet">
///   <item><c>eval-gen/src/llm-client.ts</c></item>
///   <item><c>eval-gen/src/readers/index.ts</c></item>
///   <item><c>eval-score/node/src/workiq-client.ts</c></item>
///   <item><c>eval-score/node/src/judge-providers.ts</c></item>
///   <item><c>eval-score/node/src/scorer.ts</c></item>
///   <item><c>eval-score/node/src/throttle-gate.ts</c></item>
///   <item><c>eval-score/node/src/index.ts</c></item>
/// </list>
/// </summary>
public static class EnvVars
{
    // ── EvalGen ──────────────────────────────────────────────────────────

    /// <summary>Override the LLM HTTP timeout in milliseconds. Default ~ 120000.</summary>
    public const string EvalGenLlmTimeoutMs = "EVALGEN_LLM_TIMEOUT_MS";

    /// <summary>Max retry attempts for the LLM client. Default 3. See <c>eval-gen/src/llm-client.ts</c>.</summary>
    public const string EvalGenLlmMaxAttempts = "EVALGEN_LLM_MAX_ATTEMPTS";

    /// <summary>Base backoff for the LLM retry pipeline, milliseconds. Default 2000.</summary>
    public const string EvalGenLlmBackoffMs = "EVALGEN_LLM_BACKOFF_MS";

    /// <summary>Override the LLM provider (one of the wire strings in <see cref="LLMProviders.ToWireString"/>).</summary>
    public const string EvalGenProvider = "EVALGEN_PROVIDER";

    /// <summary>External command for the <c>command</c> LLM provider.</summary>
    public const string EvalGenLlmCommand = "EVALGEN_LLM_COMMAND";

    /// <summary>Static access token for the <c>workiq-a2a</c> path.</summary>
    public const string EvalGenWorkIqToken = "EVALGEN_WORKIQ_TOKEN";

    /// <summary>Per-process Azure OpenAI endpoint (preferred over the generic <c>AZURE_OPENAI_ENDPOINT</c>).</summary>
    public const string EvalGenAzureOpenAiEndpoint = "EVALGEN_AZURE_OPENAI_ENDPOINT";

    /// <summary>Per-process Azure OpenAI key (preferred over the generic <c>AZURE_OPENAI_API_KEY</c>).</summary>
    public const string EvalGenAzureOpenAiKey = "EVALGEN_AZURE_OPENAI_KEY";

    /// <summary>Per-process model name (used by Azure OpenAI + raw command providers).</summary>
    public const string EvalGenModel = "EVALGEN_MODEL";

    /// <summary>M365 Copilot static token (for the <c>m365-copilot-api</c> provider).</summary>
    public const string EvalGenM365CopilotToken = "EVALGEN_M365_COPILOT_TOKEN";

    /// <summary>M365 Copilot tenant id (passed to <c>workiq</c> CLI for tenant pinning).</summary>
    public const string EvalGenM365TenantId = "EVALGEN_M365_TENANT_ID";

    /// <summary>M365 Copilot scope override.</summary>
    public const string EvalGenM365CopilotScope = "EVALGEN_M365_COPILOT_SCOPE";

    /// <summary>IANA timezone passed through to the M365 Copilot chat API.</summary>
    public const string EvalGenM365CopilotTimeZone = "EVALGEN_M365_COPILOT_TIME_ZONE";

    /// <summary>
    /// Opt-in: include slide master / layout text in PPTX reads. Default
    /// off because the profiler always-samples-last-record would
    /// over-weight boilerplate. See <c>eval-gen/src/readers/index.ts</c>.
    /// </summary>
    public const string EvalGenPptxIncludeMaster = "EVALGEN_PPTX_INCLUDE_MASTER";

    // ── EvalScore ────────────────────────────────────────────────────────

    /// <summary>Override the WorkIQ HTTP timeout in milliseconds.</summary>
    public const string EvalScoreWorkIqTimeoutMs = "EVALSCORE_WORKIQ_TIMEOUT_MS";

    /// <summary>Max retry attempts for transient WorkIQ failures. Default 3.</summary>
    public const string EvalScoreWorkIqMaxAttempts = "EVALSCORE_WORKIQ_MAX_ATTEMPTS";

    /// <summary>Base backoff for the WorkIQ retry pipeline, milliseconds. Default 2000.</summary>
    public const string EvalScoreWorkIqBackoffMs = "EVALSCORE_WORKIQ_BACKOFF_MS";

    /// <summary>Max backoff cap for the WorkIQ retry pipeline, milliseconds. Default 60000.</summary>
    public const string EvalScoreWorkIqBackoffMaxMs = "EVALSCORE_WORKIQ_BACKOFF_MAX_MS";

    /// <summary>Hard-capped max concurrency for the throttle gate. Default 5, hard-capped at 5 in source.</summary>
    public const string EvalScoreMaxConcurrency = "EVALSCORE_MAX_CONCURRENCY";

    /// <summary>Pre-pinned judge agent id (otherwise resolved by the provider).</summary>
    public const string EvalScoreJudgeAgentId = "EVALSCORE_JUDGE_AGENT_ID";

    /// <summary>Disable the GitHub Copilot CLI fallback judge (set to <c>true|1|yes|on</c>).</summary>
    public const string EvalScoreDisableGithubFallback = "EVALSCORE_DISABLE_GITHUB_FALLBACK";

    /// <summary>Fallback judge provider override: <c>github-copilot</c> | <c>azure-openai</c> | <c>none</c>.</summary>
    public const string EvalScoreFallbackJudgeProvider = "EVALSCORE_FALLBACK_JUDGE_PROVIDER";

    /// <summary>Override the GitHub Copilot CLI binary path for the fallback judge.</summary>
    public const string EvalScoreGithubCopilotCommand = "EVALSCORE_GITHUB_COPILOT_COMMAND";

    /// <summary>Override the GitHub Copilot CLI model identifier.</summary>
    public const string EvalScoreGithubCopilotModel = "EVALSCORE_GITHUB_COPILOT_MODEL";

    // ── WorkIQ A2A auth (shared) ─────────────────────────────────────────

    /// <summary>A2A endpoint URL. Required for the <c>workiq-a2a</c> provider.</summary>
    public const string WorkIqA2aEndpoint = "WORK_IQ_A2A_ENDPOINT";

    /// <summary>Static access token (highest-precedence auth mode).</summary>
    public const string WorkIqA2aAccessToken = "WORK_IQ_A2A_ACCESS_TOKEN";

    /// <summary>External command that prints a token to stdout (second-highest auth mode).</summary>
    public const string WorkIqA2aTokenCommand = "WORK_IQ_A2A_TOKEN_COMMAND";

    /// <summary>EvalScore-prefixed alias for <see cref="WorkIqA2aTokenCommand"/>.</summary>
    public const string EvalScoreA2aTokenCommand = "EVALSCORE_A2A_TOKEN_COMMAND";

    /// <summary>Auth mode: <c>token</c> | <c>command</c> | <c>msal</c> | <c>auto</c>.</summary>
    public const string EvalScoreA2aAuthMode = "EVALSCORE_A2A_AUTH_MODE";

    /// <summary>Legacy alias for <see cref="EvalScoreA2aAuthMode"/>.</summary>
    public const string WorkIqA2aAuthMode = "WORK_IQ_A2A_AUTH_MODE";

    /// <summary>Short-form alias for <see cref="EvalScoreA2aAuthMode"/>.</summary>
    public const string EvalScoreA2aAuth = "EVALSCORE_A2A_AUTH";

    /// <summary>Legacy short-form alias for <see cref="EvalScoreA2aAuth"/>.</summary>
    public const string WorkIqA2aAuth = "WORK_IQ_A2A_AUTH";

    /// <summary>MSAL client id (when auth mode includes MSAL).</summary>
    public const string EvalScoreA2aClientId = "EVALSCORE_A2A_CLIENT_ID";

    /// <summary>Legacy alias for <see cref="EvalScoreA2aClientId"/>.</summary>
    public const string WorkIqA2aClientId = "WORK_IQ_A2A_CLIENT_ID";

    /// <summary>MSAL tenant id.</summary>
    public const string EvalScoreA2aTenantId = "EVALSCORE_A2A_TENANT_ID";

    /// <summary>Legacy alias for <see cref="EvalScoreA2aTenantId"/>.</summary>
    public const string WorkIqA2aTenantId = "WORK_IQ_A2A_TENANT_ID";

    /// <summary>Generic tenant id (used by both EvalScore and downstream apps).</summary>
    public const string EvalScoreTenantId = "EVALSCORE_TENANT_ID";

    /// <summary>Generic tenant id (lowest-precedence fallback).</summary>
    public const string TenantId = "TENANT_ID";

    /// <summary>MSAL scopes (space-separated).</summary>
    public const string EvalScoreA2aScopes = "EVALSCORE_A2A_SCOPES";

    /// <summary>Legacy alias for <see cref="EvalScoreA2aScopes"/>.</summary>
    public const string WorkIqA2aScopes = "WORK_IQ_A2A_SCOPES";

    /// <summary>MSAL token cache file path (default: <c>~/.evalscore/msal-a2a-cache.json</c>).</summary>
    public const string EvalScoreA2aTokenCachePath = "EVALSCORE_A2A_TOKEN_CACHE_PATH";

    /// <summary>Legacy alias for <see cref="EvalScoreA2aTokenCachePath"/>.</summary>
    public const string WorkIqA2aTokenCachePath = "WORK_IQ_A2A_TOKEN_CACHE_PATH";

    /// <summary>
    /// Whether MSAL device-code flow is allowed. In the Node CLIs this
    /// defaults to <c>process.stderr.isTTY === true</c>. In the WinUI
    /// head we default this OFF and use WAM/interactive broker instead
    /// (Section 6.3 of the plan).
    /// </summary>
    public const string EvalScoreA2aAllowDeviceCode = "EVALSCORE_A2A_ALLOW_DEVICE_CODE";

    // ── Azure OpenAI (shared) ────────────────────────────────────────────

    public const string AzureOpenAiEndpoint = "AZURE_OPENAI_ENDPOINT";

    /// <summary>Legacy alias for <see cref="AzureOpenAiEndpoint"/>.</summary>
    public const string AzureAiOpenAiEndpoint = "AZURE_AI_OPENAI_ENDPOINT";

    public const string AzureOpenAiApiKey = "AZURE_OPENAI_API_KEY";

    /// <summary>Legacy alias for <see cref="AzureOpenAiApiKey"/>.</summary>
    public const string AzureAiApiKey = "AZURE_AI_API_KEY";

    public const string AzureOpenAiApiVersion = "AZURE_OPENAI_API_VERSION";

    /// <summary>Legacy alias for <see cref="AzureOpenAiApiVersion"/>.</summary>
    public const string AzureAiApiVersion = "AZURE_AI_API_VERSION";

    public const string AzureOpenAiDeployment = "AZURE_OPENAI_DEPLOYMENT";

    /// <summary>Legacy alias for <see cref="AzureOpenAiDeployment"/>.</summary>
    public const string AzureAiModelName = "AZURE_AI_MODEL_NAME";

    // ── EvalToolkit (this app — additive aliases per Section 10) ────────

    /// <summary>
    /// Workspace root for the WinUI app. Default
    /// <c>%LOCALAPPDATA%\EvalToolkit\workspace</c>. The Electron
    /// <c>eval-ui</c> uses its own <c>EVAL_UI_WORKSPACE_DIR</c> with a
    /// different default; the two are intentionally independent (plan
    /// Section 10).
    /// </summary>
    public const string EvalToolkitWorkspaceDir = "EVALTOOLKIT_WORKSPACE_DIR";

    /// <summary>
    /// Electron Eval UI's workspace dir. The WinUI app does NOT write
    /// here; it only READS this so the first-run "import jobs" wizard
    /// can probe the existing Electron-tool job folders (plan §10).
    /// </summary>
    public const string EvalUiWorkspaceDir = "EVAL_UI_WORKSPACE_DIR";
}
