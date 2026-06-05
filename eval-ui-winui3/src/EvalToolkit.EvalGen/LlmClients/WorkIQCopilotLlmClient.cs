using EvalToolkit.Core;
using EvalToolkit.WorkIQ;

namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// Microsoft 365 Copilot via the WorkIQ CLI/MCP gateway. Default M365
/// provider per <c>eval-gen/src/llm-client.ts</c>'s
/// <c>WorkIQCopilotClient</c>.
///
/// <para>Adapter strategy (per GPT-5.5 review):</para>
/// <list type="bullet">
///   <item>Reuses existing <see cref="IWorkIQClient"/> (typically
///         <see cref="CliWorkIQClient"/>) for the MCP transport so we
///         do NOT duplicate stdio handling.</item>
///   <item>Configures the underlying client's retry knobs from the
///         <c>EVALGEN_LLM_*</c> env vars to avoid double-retry stacking
///         on top of <c>WorkIQRetry</c>.</item>
///   <item>Owns the underlying client when constructed implicitly so
///         <see cref="DisposeAsync"/> cleans up the MCP process.</item>
/// </list>
/// </summary>
public sealed class WorkIQCopilotLlmClient : ILlmClient
{
    private readonly IWorkIQClient _inner;
    private readonly bool _ownsInner;

    public WorkIQCopilotLlmClient(LlmClientOptions? options = null, IWorkIQClient? inner = null)
    {
        if (inner is not null)
        {
            _inner = inner;
            _ownsInner = false;
            return;
        }

        int maxAttempts = options?.MaxAttempts
            ?? EnvHelpers.ParsePositiveIntEnv(EnvVars.EvalGenLlmMaxAttempts, 3);
        int backoffBaseMs = options?.BackoffBaseMs
            ?? EnvHelpers.ParsePositiveIntEnv(EnvVars.EvalGenLlmBackoffMs, 2000);
        int timeoutMs = options?.TimeoutMs
            ?? EnvHelpers.ParsePositiveIntEnv(EnvVars.EvalGenLlmTimeoutMs, 300_000);

        _inner = new CliWorkIQClient(new CliWorkIQClientOptions
        {
            TimeoutMs = timeoutMs,
            MaxAttempts = maxAttempts,
            BackoffBaseMs = backoffBaseMs,
        });
        _ownsInner = true;
    }

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        // TS preflight: send a tiny prompt and require non-empty reply.
        string reply = await _inner.AskAsync(
            "Reply with exactly this JSON object and no extra text: {\"ok\":true}",
            tenantId: null,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(reply))
        {
            throw new InvalidOperationException("WorkIQ authentication preflight returned an empty response");
        }
    }

    public async Task<T> GenerateStructuredAsync<T>(string prompt, string schemaDescription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(schemaDescription);

        string framed = StructuredPromptBuilder.Build(prompt, schemaDescription);
        string text = await _inner.AskAsync(framed, tenantId: null, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("WorkIQ returned an empty response");
        }
        return StructuredJsonParser.Parse<T>(text);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsInner)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}
