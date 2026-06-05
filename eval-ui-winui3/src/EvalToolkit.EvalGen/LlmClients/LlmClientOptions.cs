using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// Options bag mirroring TS <c>LLMClientOptions</c> in
/// <c>eval-gen/src/llm-client.ts</c>. Each provider only consults the
/// fields that apply to it (e.g. <see cref="Endpoint"/> + <see cref="ApiKey"/>
/// + <see cref="Model"/> are for Azure OpenAI; <see cref="M365TenantId"/>
/// is M365-only).
/// </summary>
public sealed record LlmClientOptions
{
    public string? Endpoint { get; init; }
    public string? ApiKey { get; init; }
    public string? Model { get; init; }
    public LLMProvider? Provider { get; init; }
    public string? Command { get; init; }
    public string? M365TimeZone { get; init; }
    public string? M365AccessToken { get; init; }
    public string? M365TenantId { get; init; }
    public int? MaxAttempts { get; init; }
    public int? BackoffBaseMs { get; init; }
    public int? TimeoutMs { get; init; }
}
