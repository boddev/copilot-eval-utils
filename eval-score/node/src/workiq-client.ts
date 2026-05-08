import * as fs from 'fs';
import { spawn, ChildProcess } from 'child_process';
import * as readline from 'readline';

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

/**
 * Interface for querying WorkIQ (or any LLM backend).
 * The workiq CLI provides the real implementation;
 * tests can provide a mock.
 */
export interface WorkIQClient {
  ask(question: string, tenantId?: string): Promise<string>;
  start?(tenantId?: string): Promise<void>;
  stop?(): void;
}

/**
 * Build the full prompt by prepending the system prompt (if any) to the user's question.
 */
export function buildPrompt(
  question: string,
  systemPrompt?: string,
  connectorId?: string
): string {
  const contextParts: string[] = [];

  if (connectorId) {
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
    let lastError: unknown;
    for (let attempt = 1; attempt <= this.maxAttempts; attempt++) {
      try {
        await this.startOnce();
        return;
      } catch (err) {
        lastError = err;
        this.stop();
        if (!isRetryableWorkIQError(err) || attempt === this.maxAttempts) {
          throw err;
        }
        const delayMs = computeBackoffMs(this.backoffBaseMs, attempt);
        const message = err instanceof Error ? err.message : String(err);
        console.warn(
          `[eval-score] WorkIQ MCP startup failed (attempt ${attempt}/${this.maxAttempts}): ${message}. ` +
          `Resetting MCP process and retrying in ${Math.round(delayMs)} ms.`,
        );
        await sleep(delayMs);
      }
    }
    throw lastError instanceof Error ? lastError : new Error(String(lastError));
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

  async ask(question: string, tenantId?: string): Promise<string> {
    let lastError: unknown;
    for (let attempt = 1; attempt <= this.maxAttempts; attempt++) {
      try {
        if (!this.process || this.process.killed) {
          await this.start(tenantId ?? this.tenantId);
        }
        return await this.askOnce(question);
      } catch (err) {
        lastError = err;
        if (!isRetryableWorkIQError(err) || attempt === this.maxAttempts) {
          throw err;
        }
        const delayMs = computeBackoffMs(this.backoffBaseMs, attempt);
        const message = err instanceof Error ? err.message : String(err);
        console.warn(
          `[eval-score] WorkIQ MCP call failed (attempt ${attempt}/${this.maxAttempts}): ${message}. ` +
          `Resetting MCP process and retrying in ${Math.round(delayMs)} ms.`,
        );
        this.stop();
        await sleep(delayMs);
      }
    }

    throw lastError instanceof Error ? lastError : new Error(String(lastError));
  }

  private async askOnce(question: string): Promise<string> {
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

    const content = response.result?.content;
    if (content && content.length > 0) {
      return content[0].text;
    }

    throw new Error('WorkIQ returned an empty response.');
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

  async ask(question: string, tenantId?: string): Promise<string> {
    return this.responses.get(question) ?? this.defaultResponse;
  }
}
