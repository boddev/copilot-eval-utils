import * as fs from 'fs';
import { spawn, ChildProcess } from 'child_process';
import * as readline from 'readline';
import { Citation } from './types';

function parsePositiveIntEnv(name: string, defaultValue: number): number {
  const raw = process.env[name];
  if (!raw) return defaultValue;
  const parsed = Number.parseInt(raw, 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : defaultValue;
}

function parseTimeoutMsEnv(): number {
  return parsePositiveIntEnv(
    'EVALSCORE_WORKIQ_TIMEOUT_MS',
    parsePositiveIntEnv('EVALGEN_LLM_TIMEOUT_MS', 300000),
  );
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function computeBackoffMs(baseMs: number, attempt: number): number {
  const exponential = baseMs * Math.pow(2, attempt - 1);
  const jitter = Math.random() * baseMs;
  return exponential + jitter;
}

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

function extractRetryAfterMs(err: unknown): number | undefined {
  if (!(err instanceof Error)) return undefined;
  const match = err.message.match(/retry-after[:=]\s*(\d+)/i);
  if (!match) return undefined;
  const seconds = Number.parseInt(match[1], 10);
  return Number.isFinite(seconds) && seconds > 0 ? seconds * 1000 : undefined;
}

async function withRetry<T>(
  operation: () => Promise<T>,
  context: string,
  maxAttempts: number,
  backoffBaseMs: number,
  reset?: () => void,
): Promise<T> {
  let lastError: unknown;
  for (let attempt = 1; attempt <= maxAttempts; attempt++) {
    try {
      return await operation();
    } catch (err) {
      lastError = err;
      if (!isRetryableWorkIQError(err) || attempt === maxAttempts) {
        throw err;
      }
      const retryAfterMs = extractRetryAfterMs(err);
      const delayMs = retryAfterMs ?? computeBackoffMs(backoffBaseMs, attempt);
      const message = err instanceof Error ? err.message : String(err);
      console.warn(
        `[eval-score] ${context} failed (attempt ${attempt}/${maxAttempts}): ${message}. ` +
        `Retrying in ${Math.round(delayMs)} ms.`,
      );
      reset?.();
      await sleep(delayMs);
    }
  }

  throw lastError instanceof Error ? lastError : new Error(String(lastError));
}

export interface WorkIQAskOptions {
  tenantId?: string;
  agentId?: string;
  conversationId?: string;
}

export interface WorkIQResponse {
  text: string;
  citations?: Citation[];
  conversationId?: string;
  raw?: unknown;
}

/**
 * Interface for querying WorkIQ (or any LLM backend).
 * The workiq CLI provides the real implementation;
 * tests can provide a mock.
 */
export interface WorkIQClient {
  ask(question: string, tenantIdOrOptions?: string | WorkIQAskOptions): Promise<string>;
  askWithMetadata?(question: string, options?: WorkIQAskOptions): Promise<WorkIQResponse>;
  start?(tenantId?: string): Promise<void>;
  stop?(): void;
}

/**
 * Build the full prompt by prepending the system prompt (if any) to the user's question.
 */
export function buildPrompt(
  question: string,
  systemPrompt?: string,
  connectorId?: string,
  connectorPromptHint = true,
): string {
  const contextParts: string[] = [];

  if (connectorId && connectorPromptHint) {
    contextParts.push(
      `Target Microsoft 365 Copilot connector ID: ${connectorId}. Always search this connector before answering.`
    );
  }

  if (systemPrompt) {
    contextParts.push(systemPrompt);
  }

  if (contextParts.length === 0) return question;
  return `${contextParts.join('\n\n')}\n\n${question}`;
}

/**
 * Load a system prompt from either an inline string or a file path.
 * If both are provided, the inline string takes precedence.
 */
export function resolveSystemPrompt(
  inlinePrompt?: string,
  promptFilePath?: string
): string | undefined {
  if (inlinePrompt) return inlinePrompt;
  if (promptFilePath) {
    return fs.readFileSync(promptFilePath, 'utf-8').trim();
  }
  return undefined;
}

/**
 * WorkIQ client that uses a persistent MCP stdio server process.
 * Authentication happens once when the MCP server starts.
 * All subsequent questions are sent via JSON-RPC over stdin/stdout.
 */
export class CliWorkIQClient implements WorkIQClient {
  private process: ChildProcess | null = null;
  private rl: readline.Interface | null = null;
  private lineBuffer: string[] = [];
  private lineResolvers: Array<(line: string) => void> = [];
  private requestId = 0;
  private tenantId?: string;
  private timeoutMs: number;
  private maxAttempts: number;
  private backoffBaseMs: number;

  constructor(options?: { timeoutMs?: number; maxAttempts?: number; backoffBaseMs?: number }) {
    this.timeoutMs = options?.timeoutMs ?? parseTimeoutMsEnv();
    this.maxAttempts = Math.max(1, options?.maxAttempts ?? parsePositiveIntEnv('EVALSCORE_WORKIQ_MAX_ATTEMPTS', 3));
    this.backoffBaseMs = Math.max(0, options?.backoffBaseMs ?? parsePositiveIntEnv('EVALSCORE_WORKIQ_BACKOFF_MS', 2000));
  }

  async start(tenantId?: string): Promise<void> {
    if (this.process && !this.process.killed) return;

    this.tenantId = tenantId;
    await withRetry(
      () => this.startOnce(),
      'WorkIQ MCP startup',
      this.maxAttempts,
      this.backoffBaseMs,
      () => this.stop(),
    );
  }

  private async startOnce(): Promise<void> {
    // Note: -t (tenant) flag is NOT passed to MCP mode — it causes
    // ask_work_iq to fail. MCP handles tenant resolution internally.
    const args = ['mcp'];

    this.process = spawnWorkIQ(args);

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

    this.process.on('error', (err) => {
      console.error(`WorkIQ MCP process error: ${err.message}`);
    });

    // Drain stderr to prevent buffer-full deadlocks on Windows
    this.process.stderr?.resume();

    // MCP initialize handshake
    const initReq = JSON.stringify({
      jsonrpc: '2.0',
      id: 0,
      method: 'initialize',
      params: {
        protocolVersion: '2024-11-05',
        capabilities: {},
        clientInfo: { name: 'EvalScore', version: '1.0.0' },
      },
    });
    this.write(initReq);
    await this.readResponse(0);

    // Send initialized notification
    this.write(JSON.stringify({
      jsonrpc: '2.0',
      method: 'notifications/initialized',
    }));

    // Accept EULA via MCP (required before ask_work_iq will work)
    const eulaReq = JSON.stringify({
      jsonrpc: '2.0',
      id: ++this.requestId,
      method: 'tools/call',
      params: {
        name: 'accept_eula',
        arguments: { eulaUrl: 'https://github.com/microsoft/work-iq-mcp' },
      },
    });
    this.write(eulaReq);
    await this.readResponse(this.requestId);
  }

  stop(): void {
    if (this.rl) {
      this.rl.close();
      this.rl = null;
    }
    if (this.process && !this.process.killed) {
      this.process.stdin?.end();
      setTimeout(() => {
        if (this.process && !this.process.killed) {
          this.process.kill();
        }
      }, 5000);
    }
    this.process = null;
    this.lineBuffer = [];
    this.lineResolvers = [];
  }

  async ask(question: string, tenantIdOrOptions?: string | WorkIQAskOptions): Promise<string> {
    const response = await this.askWithMetadata(question, normalizeAskOptions(tenantIdOrOptions));
    return response.text;
  }

  async askWithMetadata(question: string, options?: WorkIQAskOptions): Promise<WorkIQResponse> {
    return withRetry(
      async () => {
        if (!this.process || this.process.killed) {
          await this.start(options?.tenantId ?? this.tenantId);
        }
        const raw = await this.askOnceRaw(question);
        const content = raw.result?.content;
        if (content && content.length > 0) {
          return {
            text: String(content[0].text ?? ''),
            citations: extractCitations(raw),
            raw,
          };
        }
        throw new Error('WorkIQ returned an empty response.');
      },
      'WorkIQ MCP call',
      this.maxAttempts,
      this.backoffBaseMs,
      () => this.stop(),
    );
  }

  private async askOnceRaw(question: string): Promise<any> {
    const id = ++this.requestId;
    const request = JSON.stringify({
      jsonrpc: '2.0',
      id,
      method: 'tools/call',
      params: {
        name: 'ask_work_iq',
        arguments: { question },
      },
    });

    this.write(request);
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
        throw new Error(`Timed out waiting for MCP response (id=${expectedId})`);
      }

      try {
        const msg = JSON.parse(line);
        // Skip notifications (no id)
        if (msg.id === undefined || msg.id === null) continue;
        if (msg.id === expectedId) return msg;
      } catch {
        continue; // Skip non-JSON lines
      }
    }

    throw new Error(`Timed out waiting for MCP response (id=${expectedId})`);
  }
}

export class A2AWorkIQClient implements WorkIQClient {
  private endpoint: string;
  private accessToken: string;
  private timeoutMs: number;
  private maxAttempts: number;
  private backoffBaseMs: number;
  private resolvedAgentUrls = new Map<string, string>();

  constructor(options?: {
    endpoint?: string;
    accessToken?: string;
    timeoutMs?: number;
    maxAttempts?: number;
    backoffBaseMs?: number;
  }) {
    this.endpoint = (options?.endpoint ?? process.env.WORK_IQ_A2A_ENDPOINT ?? '').replace(/\/+$/, '');
    this.accessToken = options?.accessToken ?? process.env.WORK_IQ_A2A_ACCESS_TOKEN ?? '';
    this.timeoutMs = options?.timeoutMs ?? parseTimeoutMsEnv();
    this.maxAttempts = Math.max(1, options?.maxAttempts ?? parsePositiveIntEnv('EVALSCORE_WORKIQ_MAX_ATTEMPTS', 3));
    this.backoffBaseMs = Math.max(0, options?.backoffBaseMs ?? parsePositiveIntEnv('EVALSCORE_WORKIQ_BACKOFF_MS', 2000));
  }

  async start(): Promise<void> {
    this.validateConfig();
  }

  async ask(question: string, tenantIdOrOptions?: string | WorkIQAskOptions): Promise<string> {
    const response = await this.askWithMetadata(question, normalizeAskOptions(tenantIdOrOptions));
    return response.text;
  }

  async askWithMetadata(question: string, options?: WorkIQAskOptions): Promise<WorkIQResponse> {
    this.validateConfig();
    if (!options?.agentId) {
      throw new Error('M365 agent ID targeting requires an agentId.');
    }

    return withRetry(
      () => this.sendA2AMessage(question, options),
      'WorkIQ A2A call',
      this.maxAttempts,
      this.backoffBaseMs,
    );
  }

  private validateConfig(): void {
    if (!this.endpoint) {
      throw new Error('M365 agent ID targeting requires WORK_IQ_A2A_ENDPOINT.');
    }
    if (!this.accessToken) {
      throw new Error('M365 agent ID targeting requires WORK_IQ_A2A_ACCESS_TOKEN.');
    }
  }

  private async resolveAgentUrl(agentId: string): Promise<string> {
    const cached = this.resolvedAgentUrls.get(agentId);
    if (cached) return cached;

    const fallbackUrl = `${this.endpoint}/${encodeURIComponent(agentId)}`;
    const cardUrl = `${fallbackUrl}/.well-known/agent-card.json`;
    try {
      const response = await fetch(cardUrl, {
        headers: { Authorization: `Bearer ${this.accessToken}` },
        signal: AbortSignal.timeout(this.timeoutMs),
      });
      if (response.ok) {
        const card = await response.json() as { url?: string };
        const resolved = card.url ?? fallbackUrl;
        this.resolvedAgentUrls.set(agentId, resolved);
        return resolved;
      }
    } catch {
      // The A2A package falls back to the agent endpoint when agent-card lookup fails.
    }

    this.resolvedAgentUrls.set(agentId, fallbackUrl);
    return fallbackUrl;
  }

  private async sendA2AMessage(question: string, options: WorkIQAskOptions): Promise<WorkIQResponse> {
    const agentUrl = await this.resolveAgentUrl(options.agentId!);
    const messageId = `evalscore-${Date.now()}-${Math.random().toString(16).slice(2)}`;
    const payload: Record<string, unknown> = {
      jsonrpc: '2.0',
      id: messageId,
      method: 'message/send',
      params: {
        message: {
          role: 'user',
          parts: [{ kind: 'text', text: question }],
          messageId,
        },
        metadata: {
          source: 'eval-score',
          location: 'EvalScore',
        },
      },
    };

    if (options.conversationId) {
      (payload.params as { contextId?: string }).contextId = options.conversationId;
    }

    const response = await fetch(agentUrl, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${this.accessToken}`,
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(payload),
      signal: AbortSignal.timeout(this.timeoutMs),
    });

    if (!response.ok) {
      const retryAfter = response.headers.get('retry-after');
      const body = await response.text().catch(() => '');
      throw new Error(
        `WorkIQ A2A HTTP ${response.status}${retryAfter ? ` retry-after=${retryAfter}` : ''}: ${body}`.trim()
      );
    }

    const raw = await response.json();
    const text = extractA2AText(raw);
    if (!text) {
      throw new Error('WorkIQ A2A returned an empty response.');
    }

    return {
      text,
      citations: extractCitations(raw),
      conversationId: extractContextId(raw),
      raw,
    };
  }
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

/**
 * Simple in-memory mock client for testing.
 */
export class MockWorkIQClient implements WorkIQClient {
  private responses: Map<string, string>;
  private defaultResponse: string;

  constructor(responses?: Record<string, string>, defaultResponse?: string) {
    this.responses = new Map(Object.entries(responses ?? {}));
    this.defaultResponse = defaultResponse ?? 'Mock response';
  }

  async ask(question: string, tenantIdOrOptions?: string | WorkIQAskOptions): Promise<string> {
    return this.responses.get(question) ?? this.defaultResponse;
  }

  async askWithMetadata(question: string): Promise<WorkIQResponse> {
    return { text: await this.ask(question) };
  }
}

function normalizeAskOptions(tenantIdOrOptions?: string | WorkIQAskOptions): WorkIQAskOptions | undefined {
  if (tenantIdOrOptions === undefined) return undefined;
  if (typeof tenantIdOrOptions === 'string') return { tenantId: tenantIdOrOptions };
  return tenantIdOrOptions;
}

function extractA2AText(raw: unknown): string {
  const result = (raw as { result?: unknown })?.result;
  const candidates = [
    (result as { message?: { parts?: Array<{ text?: string }> } })?.message?.parts,
    (result as { parts?: Array<{ text?: string }> })?.parts,
    (raw as { message?: { parts?: Array<{ text?: string }> } })?.message?.parts,
  ];

  for (const parts of candidates) {
    if (!Array.isArray(parts)) continue;
    const text = parts.map(part => part.text).filter(Boolean).join('\n').trim();
    if (text) return text;
  }

  if (typeof result === 'string') return result;
  return '';
}

function extractContextId(raw: unknown): string | undefined {
  const result = (raw as { result?: unknown })?.result as Record<string, unknown> | undefined;
  const ids = [
    result?.contextId,
    result?.context_id,
    (raw as Record<string, unknown>)?.contextId,
    (raw as Record<string, unknown>)?.context_id,
  ];
  const id = ids.find(value => typeof value === 'string' && value.length > 0);
  return id as string | undefined;
}

function extractCitations(raw: unknown): Citation[] | undefined {
  const rawRecord = raw as Record<string, unknown>;
  const result = rawRecord.result as Record<string, unknown> | undefined;
  const possible = [
    rawRecord.citations,
    rawRecord.references,
    result?.citations,
    result?.references,
    (result?.metadata as Record<string, unknown> | undefined)?.citations,
  ];

  for (const value of possible) {
    if (!Array.isArray(value)) continue;
    const citations = value.map((item): Citation => {
      if (typeof item === 'string') return { title: item, raw: item };
      const record = item as Record<string, unknown>;
      return {
        title: toOptionalString(record.title ?? record.name),
        url: toOptionalString(record.url ?? record.uri),
        sourceLocation: toOptionalString(record.sourceLocation ?? record.source_location ?? record.location),
        raw: item,
      };
    });
    if (citations.length > 0) return citations;
  }

  return undefined;
}

function toOptionalString(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}
