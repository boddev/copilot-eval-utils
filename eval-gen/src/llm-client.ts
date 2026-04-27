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
 * Create the configured LLM provider.
 */
export function createLLMClient(options?: LLMClientOptions): LLMClient {
  const provider = options?.provider
    ?? (process.env.EVALGEN_PROVIDER as LLMProvider | undefined)
    ?? 'm365-copilot';

  switch (provider) {
    case 'm365-copilot':
      return new WorkIQCopilotClient();
    case 'm365-copilot-api':
      return new Microsoft365CopilotChatClient(options);
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

  constructor(options?: { timeoutMs?: number }) {
    this.timeoutMs = options?.timeoutMs ?? 300000;
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

  constructor(options?: LLMClientOptions) {
    this.accessToken = options?.m365AccessToken ?? process.env.EVALGEN_M365_COPILOT_TOKEN ?? '';
    this.hasProvidedAccessToken = this.accessToken.length > 0;
    this.tenantId = options?.m365TenantId ?? process.env.EVALGEN_M365_TENANT_ID;
    this.timeZone = options?.m365TimeZone
      ?? process.env.EVALGEN_M365_COPILOT_TIME_ZONE
      ?? Intl.DateTimeFormat().resolvedOptions().timeZone
      ?? 'UTC';
  }

  async authenticate(): Promise<void> {
    await this.createConversationWithRetry('authentication preflight');
  }

  async generateStructured<T>(prompt: string, schemaDescription: string): Promise<T> {
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
