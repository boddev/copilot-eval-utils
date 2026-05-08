import { LLMClient } from './types';
import { ChildProcess, spawn } from 'child_process';
import * as readline from 'readline';
import { LLMProvider } from './types';

interface LLMClientOptions {
  endpoint?: string;
  apiKey?: string;
  model?: string;
  provider?: LLMProvider;
  command?: string;
  m365TimeZone?: string;
  m365AccessToken?: string;
  m365TenantId?: string;
  maxAttempts?: number;
  backoffBaseMs?: number;
}

const M365_COPILOT_SCOPES = [
  'https://graph.microsoft.com/Sites.Read.All',
  'https://graph.microsoft.com/Mail.Read',
  'https://graph.microsoft.com/People.Read.All',
  'https://graph.microsoft.com/OnlineMeetingTranscript.Read.All',
  'https://graph.microsoft.com/Chat.Read',
  'https://graph.microsoft.com/ChannelMessage.Read.All',
  'https://graph.microsoft.com/ExternalItem.Read.All',
];

/**
 * Parse the WorkIQ MCP request timeout from EVALGEN_LLM_TIMEOUT_MS.
 * Falls back to 300000 ms (5 minutes) when unset or invalid.
 */
function parseTimeoutMsEnv(): number {
  const raw = process.env.EVALGEN_LLM_TIMEOUT_MS;
  if (!raw) return 300000;
  const parsed = Number.parseInt(raw, 10);
  if (!Number.isFinite(parsed) || parsed <= 0) return 300000;
  return parsed;
}

/**
 * Parse a positive integer env var, falling back to a default when unset/invalid.
 */
function parsePositiveIntEnv(name: string, defaultValue: number): number {
  const raw = process.env[name];
  if (!raw) return defaultValue;
  const parsed = Number.parseInt(raw, 10);
  if (!Number.isFinite(parsed) || parsed <= 0) return defaultValue;
  return parsed;
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * Jittered exponential backoff: baseMs * 2^(attempt-1) + random(0..baseMs).
 */
function computeBackoffMs(baseMs: number, attempt: number): number {
  const exponential = baseMs * Math.pow(2, attempt - 1);
  const jitter = Math.random() * baseMs;
  return exponential + jitter;
}

/**
 * Returns true when an M365 Copilot Chat API failure is likely transient.
 * Retries on 408/429/500/502/503/504, network errors, and empty-message
 * responses. 401/403/400 and other 4xx are NOT retryable (auth or input errors).
 */
export function isRetryableCopilotApiError(err: unknown): boolean {
  if (!(err instanceof Error)) return false;
  if (err instanceof GraphApiError) {
    return err.status === 408 || err.status === 429 || (err.status >= 500 && err.status <= 599);
  }
  const message = err.message.toLowerCase();
  if (message.includes('did not return a conversation id') || message.includes('returned no message text')) {
    return true;
  }
  return (
    message.includes('timed out') ||
    message.includes('timeout') ||
    message.includes('econnreset') ||
    message.includes('epipe') ||
    message.includes('etimedout') ||
    message.includes('socket hang up') ||
    message.includes('fetch failed') ||
    message.includes('network')
  );
}

/**
 * Returns true when a WorkIQ MCP failure is likely transient and worth retrying.
 * Retries on timeout, broken stdio pipe, process-not-running, generic transport
 * errors, and HTTP 429/503 echoed by WorkIQ. Authentication, EULA, and
 * client-side parsing errors are NOT retryable.
 */
export function isRetryableWorkIQError(err: unknown): boolean {
  if (!(err instanceof Error)) return false;
  const message = err.message.toLowerCase();
  if (message.includes('eula') || message.includes('unauthor') || message.includes('forbidden') || message.includes('401') || message.includes('403')) {
    return false;
  }
  return (
    message.includes('timed out') ||
    message.includes('timeout') ||
    message.includes('mcp process') ||
    message.includes('process is not running') ||
    message.includes('process exited') ||
    message.includes('econnreset') ||
    message.includes('epipe') ||
    message.includes('etimedout') ||
    message.includes('socket hang up') ||
    message.includes('429') ||
    message.includes('rate limit') ||
    message.includes('throttl') ||
    message.includes('503') ||
    message.includes('502') ||
    message.includes('504') ||
    message.includes('temporarily unavailable') ||
    message.includes('empty response')
  );
}

/**
 * Create the configured LLM provider.
 */
export function createLLMClient(options?: LLMClientOptions): LLMClient {
  const provider = options?.provider
    ?? (process.env.EVALGEN_PROVIDER as LLMProvider | undefined)
    ?? 'm365-copilot';

  switch (provider) {
    case 'm365-copilot':
      return new WorkIQCopilotClient({
        timeoutMs: parseTimeoutMsEnv(),
        maxAttempts: parsePositiveIntEnv('EVALGEN_LLM_MAX_ATTEMPTS', 3),
        backoffBaseMs: parsePositiveIntEnv('EVALGEN_LLM_BACKOFF_MS', 2000),
      });
    case 'm365-copilot-api':
      return new Microsoft365CopilotChatClient({
        ...options,
        maxAttempts: parsePositiveIntEnv('EVALGEN_LLM_MAX_ATTEMPTS', 3),
        backoffBaseMs: parsePositiveIntEnv('EVALGEN_LLM_BACKOFF_MS', 2000),
      });
    case 'workiq-a2a':
      return new WorkIQA2AClient({
        accessToken: process.env.EVALGEN_WORKIQ_TOKEN,
        timeoutMs: parseTimeoutMsEnv(),
        maxAttempts: parsePositiveIntEnv('EVALGEN_LLM_MAX_ATTEMPTS', 3),
        backoffBaseMs: parsePositiveIntEnv('EVALGEN_LLM_BACKOFF_MS', 2000),
        timeZone: options?.m365TimeZone,
      });
    case 'azure-openai':
      return new AzureOpenAIClient(options);
    case 'github-copilot':
      return new GitHubCopilotCliClient(options);
    case 'command':
      return new CommandLLMClient(options?.command ?? process.env.EVALGEN_LLM_COMMAND ?? '');
    default:
      throw new Error(`Unsupported LLM provider: ${provider}`);
  }
}

/**
 * Microsoft 365 Copilot through the WorkIQ CLI/MCP gateway.
 *
 * WorkIQ owns the M365 authentication/session flow for this repository. This is
 * the default M365 provider because it avoids requiring Azure CLI or a custom
 * Graph token flow for users who only have Microsoft 365 access.
 */
export class WorkIQCopilotClient implements LLMClient {
  private process: ChildProcess | null = null;
  private rl: readline.Interface | null = null;
  private lineBuffer: string[] = [];
  private lineResolvers: Array<(line: string) => void> = [];
  private requestId = 0;
  private timeoutMs: number;
  private maxAttempts: number;
  private backoffBaseMs: number;

  constructor(options?: { timeoutMs?: number; maxAttempts?: number; backoffBaseMs?: number }) {
    this.timeoutMs = options?.timeoutMs ?? 300000;
    this.maxAttempts = Math.max(1, options?.maxAttempts ?? 3);
    this.backoffBaseMs = Math.max(0, options?.backoffBaseMs ?? 2000);
  }

  async authenticate(): Promise<void> {
    await this.start();
    const response = await this.askRaw('Reply with exactly this JSON object and no extra text: {"ok":true}');
    if (!response.trim()) {
      throw new Error('WorkIQ authentication preflight returned an empty response');
    }
  }

  async generateStructured<T>(prompt: string, schemaDescription: string): Promise<T> {
    const output = await this.askRaw(buildStructuredPrompt(prompt, schemaDescription));
    return parseStructuredJson<T>(output);
  }

  close(): void {
    if (this.rl) {
      this.rl.close();
      this.rl = null;
    }
    if (this.process && !this.process.killed) {
      this.process.stdin?.end();
      this.process.kill();
    }
    this.process = null;
    this.lineBuffer = [];
    this.lineResolvers = [];
  }

  private async start(): Promise<void> {
    if (this.process && !this.process.killed) return;

    this.process = spawnWorkIQ(['mcp']);
    this.rl = readline.createInterface({ input: this.process.stdout! });
    this.rl.on('line', (line: string) => {
      const trimmed = line.trim();
      if (!trimmed) return;
      if (this.lineResolvers.length > 0) {
        this.lineResolvers.shift()!(trimmed);
      } else {
        this.lineBuffer.push(trimmed);
      }
    });

    this.process.stderr?.resume();
    this.process.on('error', (err) => {
      throw new Error(`WorkIQ MCP process error: ${err.message}`);
    });
    this.process.on('exit', (code) => {
      if (code !== null && code !== 0) {
        this.lineResolvers.splice(0).forEach(resolve => resolve(JSON.stringify({
          id: this.requestId,
          error: { message: `WorkIQ MCP process exited with code ${code}` },
        })));
      }
    });

    this.write(JSON.stringify({
      jsonrpc: '2.0',
      id: 0,
      method: 'initialize',
      params: {
        protocolVersion: '2024-11-05',
        capabilities: {},
        clientInfo: { name: 'eval-gen', version: '1.0.0' },
      },
    }));
    await this.readResponse(0);

    this.write(JSON.stringify({
      jsonrpc: '2.0',
      method: 'notifications/initialized',
    }));

    await this.callTool('accept_eula', { eulaUrl: 'https://github.com/microsoft/work-iq-mcp' });
  }

  private async askRaw(question: string): Promise<string> {
    let lastError: unknown;
    for (let attempt = 1; attempt <= this.maxAttempts; attempt++) {
      try {
        await this.start();
        const response = await this.callTool('ask_work_iq', { question });
        const content = response.result?.content;
        if (response.result?.isError) {
          throw new Error(`WorkIQ tool error: ${content?.[0]?.text ?? 'unknown error'}`);
        }
        if (content && content.length > 0 && typeof content[0].text === 'string') {
          return content[0].text;
        }
        throw new Error('WorkIQ returned an empty response');
      } catch (err) {
        lastError = err;
        if (!isRetryableWorkIQError(err) || attempt === this.maxAttempts) {
          throw err;
        }
        const delayMs = computeBackoffMs(this.backoffBaseMs, attempt);
        const message = err instanceof Error ? err.message : String(err);
        console.warn(
          `[eval-gen] WorkIQ MCP call failed (attempt ${attempt}/${this.maxAttempts}): ${message}. ` +
          `Resetting MCP process and retrying in ${Math.round(delayMs)} ms.`,
        );
        this.close();
        await sleep(delayMs);
      }
    }
    throw lastError instanceof Error ? lastError : new Error(String(lastError));
  }

  private async callTool(name: string, args: Record<string, unknown>): Promise<any> {
    const id = ++this.requestId;
    this.write(JSON.stringify({
      jsonrpc: '2.0',
      id,
      method: 'tools/call',
      params: {
        name,
        arguments: args,
      },
    }));

    const response = await this.readResponse(id);
    if (response.error) {
      throw new Error(`WorkIQ error: ${response.error.message}`);
    }
    return response;
  }

  private write(data: string): void {
    this.process!.stdin!.write(data + '\n');
  }

  private readLine(): Promise<string> {
    if (this.lineBuffer.length > 0) {
      return Promise.resolve(this.lineBuffer.shift()!);
    }
    if (!this.process || this.process.killed) {
      return Promise.reject(new Error('WorkIQ MCP process is not running'));
    }
    return new Promise((resolve) => {
      this.lineResolvers.push(resolve);
    });
  }

  private async readResponse(expectedId: number): Promise<any> {
    const deadline = Date.now() + this.timeoutMs;

    while (Date.now() < deadline) {
      const linePromise = this.readLine();
      const timeoutPromise = new Promise<null>((resolve) =>
        setTimeout(() => resolve(null), Math.min(this.timeoutMs, deadline - Date.now()))
      );

      const line = await Promise.race([linePromise, timeoutPromise]);
      if (line === null) {
        throw new Error(`Timed out waiting for WorkIQ MCP response (id=${expectedId})`);
      }

      try {
        const msg = JSON.parse(line);
        if (msg.id === undefined || msg.id === null) continue;
        if (msg.id === expectedId) return msg;
      } catch {
        continue;
      }
    }

    throw new Error(`Timed out waiting for WorkIQ MCP response (id=${expectedId})`);
  }
}

/**
 * Microsoft Work IQ A2A (Agent-to-Agent) public preview API.
 *
 * Direct HTTPS replacement for the WorkIQ MCP transport. Calls the Work IQ
 * Gateway at https://workiq.svc.cloud.microsoft/a2a/ using JSON-RPC 2.0 with
 * the A2A v1.0 wire format. Same backend intelligence as WorkIQ MCP, but
 * removes the stdio child process as a failure point.
 *
 * One-time admin setup (see the Work IQ API quickstart):
 *   1. Create the Work IQ service principal (appId fdcc1f02-fc51-4226-8753-f668596af7f7)
 *   2. Register a client app with delegated permission `WorkIQAgent.Ask`
 *   3. Grant admin consent
 *
 * Authentication: a delegated bearer token issued for your registered app with
 * audience `api://workiq.svc.cloud.microsoft` is required. Provide it via
 * EVALGEN_WORKIQ_TOKEN. Tokens issued for the Azure CLI or other arbitrary
 * first-party clients are rejected by the Work IQ Gateway.
 *
 * Reference: https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/work-iq-api-quickstart
 */
export class WorkIQA2AClient implements LLMClient {
  private static readonly ENDPOINT = 'https://workiq.svc.cloud.microsoft/a2a/';

  private accessToken: string;
  private timeoutMs: number;
  private maxAttempts: number;
  private backoffBaseMs: number;
  private timeZone: string;
  private contextId?: string;

  constructor(options?: {
    accessToken?: string;
    timeoutMs?: number;
    maxAttempts?: number;
    backoffBaseMs?: number;
    timeZone?: string;
  }) {
    this.accessToken = options?.accessToken ?? process.env.EVALGEN_WORKIQ_TOKEN ?? '';
    this.timeoutMs = options?.timeoutMs ?? 300000;
    this.maxAttempts = Math.max(1, options?.maxAttempts ?? 3);
    this.backoffBaseMs = Math.max(0, options?.backoffBaseMs ?? 2000);
    this.timeZone = options?.timeZone
      ?? process.env.EVALGEN_M365_COPILOT_TIME_ZONE
      ?? Intl.DateTimeFormat().resolvedOptions().timeZone
      ?? 'UTC';

    if (!this.accessToken) {
      throw new Error(
        'Work IQ A2A requires a delegated bearer token. Set EVALGEN_WORKIQ_TOKEN to a JWT issued for your app ' +
        'registration with the WorkIQAgent.Ask scope and audience api://workiq.svc.cloud.microsoft. ' +
        'See https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/work-iq-api-quickstart for setup steps.',
      );
    }
  }

  async authenticate(): Promise<void> {
    const reply = await this.sendMessage('Reply with exactly this JSON object and no extra text: {"ok":true}');
    if (!reply.trim()) {
      throw new Error('Work IQ A2A authentication preflight returned an empty response');
    }
  }

  async generateStructured<T>(prompt: string, schemaDescription: string): Promise<T> {
    const reply = await this.sendMessage(buildStructuredPrompt(prompt, schemaDescription));
    return parseStructuredJson<T>(reply);
  }

  private async sendMessage(text: string): Promise<string> {
    let lastError: unknown;
    for (let attempt = 1; attempt <= this.maxAttempts; attempt++) {
      try {
        return await this.sendMessageOnce(text);
      } catch (err) {
        lastError = err;
        if (!isRetryableA2AError(err) || attempt === this.maxAttempts) {
          throw err;
        }
        const delayMs = computeBackoffMs(this.backoffBaseMs, attempt);
        const message = err instanceof Error ? err.message : String(err);
        console.warn(
          `[eval-gen] Work IQ A2A call failed (attempt ${attempt}/${this.maxAttempts}): ${message}. ` +
          `Retrying in ${Math.round(delayMs)} ms.`,
        );
        await sleep(delayMs);
      }
    }
    throw lastError instanceof Error ? lastError : new Error(String(lastError));
  }

  private async sendMessageOnce(text: string): Promise<string> {
    const requestId = randomGuid();
    const messageId = randomGuid();
    const message: Record<string, unknown> = {
      role: 'ROLE_USER',
      messageId,
      parts: [{ text }],
      metadata: {
        Location: {
          timeZone: this.timeZone,
          timeZoneOffset: -new Date().getTimezoneOffset(),
        },
      },
    };
    if (this.contextId) {
      message.contextId = this.contextId;
    }

    const body = {
      jsonrpc: '2.0',
      id: requestId,
      method: 'SendMessage',
      params: { message },
    };

    const controller = new AbortController();
    const timeoutHandle = setTimeout(() => controller.abort(), this.timeoutMs);
    let response: Response;
    try {
      response = await fetch(WorkIQA2AClient.ENDPOINT, {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${this.accessToken}`,
          'Content-Type': 'application/json',
          'Accept': 'application/json',
          'A2A-Version': '1.0',
        },
        body: JSON.stringify(body),
        signal: controller.signal,
      });
    } catch (err) {
      if ((err as { name?: string }).name === 'AbortError') {
        throw new Error(`Work IQ A2A request timed out after ${this.timeoutMs} ms`);
      }
      throw err;
    } finally {
      clearTimeout(timeoutHandle);
    }

    if (!response.ok) {
      const errorText = await response.text();
      throw new WorkIQA2AError(response.status, errorText);
    }

    const json = await response.json() as {
      result?: {
        task?: {
          contextId?: string;
          status?: { state?: string; message?: { parts?: Array<{ text?: string }> } };
          artifacts?: Array<{ parts?: Array<{ text?: string }> }>;
        };
      };
      error?: { code: number; message: string };
    };

    if (json.error) {
      throw new WorkIQA2AError(0, `Work IQ A2A JSON-RPC error ${json.error.code}: ${json.error.message}`);
    }

    const task = json.result?.task;
    if (!task) {
      throw new Error('Work IQ A2A response is missing result.task');
    }

    if (task.contextId) {
      this.contextId = task.contextId;
    }

    const state = task.status?.state;
    if (state && state !== 'TASK_STATE_COMPLETED') {
      const detail = task.status?.message?.parts?.find(p => p.text)?.text;
      throw new Error(`Work IQ A2A task ended in state ${state}${detail ? `: ${detail}` : ''}`);
    }

    const artifactText = task.artifacts
      ?.flatMap(a => a.parts ?? [])
      .find(p => p && typeof p.text === 'string')
      ?.text;
    if (!artifactText) {
      throw new Error('Work IQ A2A task completed but contained no text artifact');
    }
    return artifactText;
  }
}

class WorkIQA2AError extends Error {
  constructor(public readonly status: number, public readonly responseBody: string) {
    super(status > 0
      ? `Work IQ A2A HTTP ${status}: ${responseBody}`
      : responseBody);
  }
}

/**
 * Returns true when a Work IQ A2A failure is likely transient.
 * Retries on 408/429/5xx, network errors, request timeouts, and transient
 * task-state failures. 4xx (other than 408/429) are NOT retryable — those
 * indicate auth, scope, or input problems.
 */
export function isRetryableA2AError(err: unknown): boolean {
  if (!(err instanceof Error)) return false;
  if (err instanceof WorkIQA2AError) {
    if (err.status === 0) return false;
    return err.status === 408 || err.status === 429 || (err.status >= 500 && err.status <= 599);
  }
  const message = err.message.toLowerCase();
  return (
    message.includes('timed out') ||
    message.includes('timeout') ||
    message.includes('econnreset') ||
    message.includes('epipe') ||
    message.includes('etimedout') ||
    message.includes('socket hang up') ||
    message.includes('fetch failed') ||
    message.includes('network') ||
    message.includes('missing result.task') ||
    message.includes('no text artifact')
  );
}

function randomGuid(): string {
  const bytes = new Uint8Array(16);
  for (let i = 0; i < bytes.length; i++) bytes[i] = Math.floor(Math.random() * 256);
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/**
 * Azure OpenAI client wrapper.
 * Uses REST API directly to avoid SDK version churn.
 */
export class AzureOpenAIClient implements LLMClient {
  private endpoint: string;
  private apiKey: string;
  private model: string;

  constructor(options?: LLMClientOptions) {
    this.endpoint = options?.endpoint
      ?? process.env.EVALGEN_AZURE_OPENAI_ENDPOINT
      ?? '';
    this.apiKey = options?.apiKey
      ?? process.env.EVALGEN_AZURE_OPENAI_KEY
      ?? process.env.AZURE_OPENAI_API_KEY
      ?? '';
    this.model = options?.model
      ?? process.env.EVALGEN_MODEL
      ?? 'gpt-4o';

    if (!this.endpoint) {
      throw new Error(
        'Azure OpenAI endpoint required. Set EVALGEN_AZURE_OPENAI_ENDPOINT or pass endpoint option.'
      );
    }
    if (!this.apiKey) {
      throw new Error(
        'Azure OpenAI API key required. Set EVALGEN_AZURE_OPENAI_KEY or pass apiKey option.'
      );
    }
  }

  async generateStructured<T>(prompt: string, schemaDescription: string): Promise<T> {
    const url = `${this.endpoint.replace(/\/$/, '')}/openai/deployments/${this.model}/chat/completions?api-version=2024-10-21`;

    const body = {
      messages: [
        {
          role: 'system' as const,
          content: `You are a precise data analysis assistant. Always respond with valid JSON matching the requested schema. ${schemaDescription}`,
        },
        { role: 'user' as const, content: prompt },
      ],
      temperature: 0.7,
      max_tokens: 16000,
      response_format: { type: 'json_object' },
    };

    const response = await fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'api-key': this.apiKey,
      },
      body: JSON.stringify(body),
    });

    if (!response.ok) {
      const errorText = await response.text();
      throw new Error(`Azure OpenAI API error (${response.status}): ${errorText}`);
    }

    const data = await response.json() as {
      choices: Array<{ message: { content: string } }>;
    };
    const content = data.choices?.[0]?.message?.content;
    if (!content) {
      throw new Error('Azure OpenAI returned empty response');
    }

    return JSON.parse(content) as T;
  }
}

/**
 * Microsoft 365 Copilot Chat API provider.
 *
 * The API is delegated-only and currently under Microsoft Graph beta. Provide a
 * delegated token with EVALGEN_M365_COPILOT_TOKEN, or sign in with Azure CLI so
 * eval-gen can request a Microsoft Graph token via `az account get-access-token`.
 */
export class Microsoft365CopilotChatClient implements LLMClient {
  private accessToken: string;
  private readonly hasProvidedAccessToken: boolean;
  private tenantId?: string;
  private timeZone: string;
  private maxAttempts: number;
  private backoffBaseMs: number;

  constructor(options?: LLMClientOptions) {
    this.accessToken = options?.m365AccessToken ?? process.env.EVALGEN_M365_COPILOT_TOKEN ?? '';
    this.hasProvidedAccessToken = this.accessToken.length > 0;
    this.tenantId = options?.m365TenantId ?? process.env.EVALGEN_M365_TENANT_ID;
    this.timeZone = options?.m365TimeZone
      ?? process.env.EVALGEN_M365_COPILOT_TIME_ZONE
      ?? Intl.DateTimeFormat().resolvedOptions().timeZone
      ?? 'UTC';
    this.maxAttempts = Math.max(1, options?.maxAttempts ?? 3);
    this.backoffBaseMs = Math.max(0, options?.backoffBaseMs ?? 2000);
  }

  async authenticate(): Promise<void> {
    await this.createConversationWithRetry('authentication preflight');
  }

  async generateStructured<T>(prompt: string, schemaDescription: string): Promise<T> {
    let lastError: unknown;
    for (let attempt = 1; attempt <= this.maxAttempts; attempt++) {
      try {
        const conversation = await this.createConversationWithRetry('conversation creation');
        if (!conversation.id) {
          throw new Error('Microsoft 365 Copilot Chat API did not return a conversation id');
        }

        const token = await this.getAccessToken();
        const response = await graphFetch<{
          messages?: Array<{ text?: string }>;
        }>(
          `https://graph.microsoft.com/beta/copilot/conversations/${conversation.id}/chat`,
          token,
          {
            message: {
              text: buildStructuredPrompt(prompt, schemaDescription),
            },
            locationHint: {
              timeZone: this.timeZone,
            },
          },
          200,
        );

        const content = [...(response.messages ?? [])].reverse().find(m => m.text)?.text;
        if (!content) {
          throw new Error('Microsoft 365 Copilot Chat API returned no message text');
        }

        return parseStructuredJson<T>(content);
      } catch (err) {
        lastError = err;
        if (!isRetryableCopilotApiError(err) || attempt === this.maxAttempts) {
          throw err;
        }
        const delayMs = computeBackoffMs(this.backoffBaseMs, attempt);
        const message = err instanceof Error ? err.message : String(err);
        console.warn(
          `[eval-gen] M365 Copilot Chat API call failed (attempt ${attempt}/${this.maxAttempts}): ${message}. ` +
          `Retrying in ${Math.round(delayMs)} ms.`,
        );
        await sleep(delayMs);
      }
    }
    throw lastError instanceof Error ? lastError : new Error(String(lastError));
  }

  private async createConversationWithRetry(operation: string): Promise<{ id?: string }> {
    try {
      return await this.createConversation();
    } catch (error) {
      if (
        this.hasProvidedAccessToken
        || !(error instanceof GraphApiError)
        || (error.status !== 401 && error.status !== 403)
      ) {
        throw enrichM365AuthError(error);
      }

      process.stderr.write(`  Microsoft 365 Copilot auth check returned ${error.status}; running az login with Copilot Graph scopes...\n`);
      await runAzureLogin(this.tenantId);
      this.accessToken = '';

      try {
        return await this.createConversation();
      } catch (retryError) {
        throw enrichM365AuthError(retryError);
      }
    }
  }

  private async createConversation(): Promise<{ id?: string }> {
    const token = await this.getAccessToken();
    return await graphFetch<{ id?: string }>(
      'https://graph.microsoft.com/beta/copilot/conversations',
      token,
      {},
      201,
    );
  }

  private async getAccessToken(): Promise<string> {
    if (!this.accessToken) {
      try {
        this.accessToken = await getAzureCliGraphToken(this.tenantId);
      } catch (error) {
        if (this.hasProvidedAccessToken) {
          throw error;
        }

        process.stderr.write('  Azure CLI could not acquire a Microsoft Graph token; running az login with Copilot Graph scopes...\n');
        await runAzureLogin(this.tenantId);
        this.accessToken = await getAzureCliGraphToken(this.tenantId);
      }
    }
    return this.accessToken;
  }
}

/**
 * GitHub Copilot CLI provider.
 *
 * Uses `gh copilot -- -p ... --silent --no-color` so existing GitHub Copilot
 * authentication is reused and only the model response is captured.
 */
export class GitHubCopilotCliClient implements LLMClient {
  private model?: string;

  constructor(options?: LLMClientOptions) {
    this.model = options?.model ?? process.env.EVALGEN_MODEL;
  }

  async generateStructured<T>(prompt: string, schemaDescription: string): Promise<T> {
    const args = ['copilot', '--', '-p', buildStructuredPrompt(prompt, schemaDescription), '--silent', '--no-color'];
    if (this.model) {
      args.push('--model', this.model);
    }

    const output = await runProcess('gh', args);
    return parseStructuredJson<T>(output);
  }
}

/**
 * Custom command provider.
 *
 * The command receives JSON on stdin with `prompt` and `schemaDescription`, and
 * must print a JSON object matching the requested schema to stdout.
 */
export class CommandLLMClient implements LLMClient {
  private command: string;

  constructor(command: string) {
    if (!command) {
      throw new Error('Command provider requires --llm-command or EVALGEN_LLM_COMMAND');
    }
    this.command = command;
  }

  async generateStructured<T>(prompt: string, schemaDescription: string): Promise<T> {
    const output = await runProcess(this.command, [], JSON.stringify({ prompt, schemaDescription }), true);
    return parseStructuredJson<T>(output);
  }
}

function buildStructuredPrompt(prompt: string, schemaDescription: string): string {
  return `You are a precise data analysis assistant. Always respond with valid JSON matching the requested schema.

${schemaDescription}

${prompt}`;
}

async function graphFetch<T>(
  url: string,
  token: string,
  body: unknown,
  expectedStatus: number,
): Promise<T> {
  const response = await fetch(url, {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${token}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(body),
  });

  if (response.status !== expectedStatus) {
    const errorText = await response.text();
    throw new GraphApiError(response.status, errorText);
  }

  return await response.json() as T;
}

class GraphApiError extends Error {
  constructor(public readonly status: number, public readonly responseBody: string) {
    super(`Microsoft 365 Copilot Chat API error (${status}): ${responseBody}`);
  }
}

async function getAzureCliGraphToken(tenantId?: string): Promise<string> {
  const args = [
    'account',
    'get-access-token',
    '--scope',
    getM365CopilotScopes(),
    '--query',
    'accessToken',
    '-o',
    'tsv',
  ];
  if (tenantId) {
    args.push('--tenant', tenantId);
  }

  const output = await runProcess('az', args, undefined, process.platform === 'win32');

  const token = output.trim();
  if (!token) {
    throw new Error('Azure CLI did not return a Microsoft Graph access token');
  }
  return token;
}

async function runAzureLogin(tenantId?: string): Promise<void> {
  const args = ['login', '--scope', getM365CopilotScopes()];
  if (tenantId) {
    args.push('--tenant', tenantId);
  }

  await runProcess('az', args, undefined, process.platform === 'win32');
}

function getM365CopilotScopes(): string {
  return process.env.EVALGEN_M365_COPILOT_SCOPE ?? M365_COPILOT_SCOPES.join(' ');
}

function enrichM365AuthError(error: unknown): Error {
  if (!(error instanceof GraphApiError) || (error.status !== 401 && error.status !== 403)) {
    return error instanceof Error ? error : new Error(String(error));
  }

  return new Error(
    `Microsoft 365 Copilot authentication failed (${error.status}). ` +
    'Run `az login` with a work/school account that has a Microsoft 365 Copilot license and delegated Graph consent for ' +
    'Sites.Read.All, Mail.Read, People.Read.All, OnlineMeetingTranscript.Read.All, Chat.Read, ChannelMessage.Read.All, and ExternalItem.Read.All. ' +
    'If you use a specific tenant, pass --m365-tenant or set EVALGEN_M365_TENANT_ID. ' +
    `Response: ${error.responseBody}`
  );
}

function runProcess(command: string, args: string[], input?: string, shell = false): Promise<string> {
  return new Promise((resolve, reject) => {
    const child = shell
      ? spawn(buildShellCommand(command, args), { shell: true, stdio: ['pipe', 'pipe', 'pipe'] })
      : spawn(command, args, { stdio: ['pipe', 'pipe', 'pipe'] });

    const stdout: Buffer[] = [];
    const stderr: Buffer[] = [];

    child.stdout.on('data', data => stdout.push(Buffer.from(data)));
    child.stderr.on('data', data => stderr.push(Buffer.from(data)));
    child.on('error', reject);
    child.on('close', code => {
      const output = Buffer.concat(stdout).toString('utf-8');
      const errorOutput = Buffer.concat(stderr).toString('utf-8');
      if (code !== 0) {
        reject(new Error(`${command} exited with code ${code}: ${errorOutput || output}`));
        return;
      }
      resolve(output);
    });

    if (input) {
      child.stdin.write(input);
    }
    child.stdin.end();
  });
}

function spawnWorkIQ(args: string[]): ChildProcess {
  if (process.platform === 'win32') {
    return spawn(buildShellCommand('workiq', args), { shell: true, stdio: ['pipe', 'pipe', 'pipe'] });
  }
  return spawn('workiq', args, { stdio: ['pipe', 'pipe', 'pipe'] });
}

function buildShellCommand(command: string, args: string[]): string {
  if (args.length === 0) return command;
  return [command, ...args.map(quoteShellArg)].join(' ');
}

function quoteShellArg(value: string): string {
  if (!/[\s"&|<>^]/.test(value)) return value;
  return `"${value.replace(/"/g, '\\"')}"`;
}

export function parseStructuredJson<T>(content: string): T {
  const stripped = content
    .replace(/\u001b\[[0-9;]*m/g, '')
    .trim();

  try {
    return JSON.parse(stripped) as T;
  } catch {
    // Continue with extraction below.
  }

  const fenced = stripped.match(/```(?:json)?\s*([\s\S]*?)```/i);
  if (fenced?.[1]) {
    return JSON.parse(fenced[1].trim()) as T;
  }

  const start = stripped.indexOf('{');
  const end = stripped.lastIndexOf('}');
  if (start >= 0 && end > start) {
    return JSON.parse(stripped.slice(start, end + 1)) as T;
  }

  throw new Error('LLM response did not contain a JSON object');
}

/**
 * Mock LLM client for testing.
 */
export class MockLLMClient implements LLMClient {
  private responses: Map<string, unknown> = new Map();
  private defaultResponse: unknown;

  constructor(defaultResponse?: unknown) {
    this.defaultResponse = defaultResponse ?? {};
  }

  setResponse(promptSubstring: string, response: unknown): void {
    this.responses.set(promptSubstring, response);
  }

  async generateStructured<T>(prompt: string, _schema: string): Promise<T> {
    for (const [substring, response] of this.responses) {
      if (prompt.includes(substring)) {
        return response as T;
      }
    }
    return this.defaultResponse as T;
  }
}
