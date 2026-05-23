import { spawn } from 'child_process';
import { EvalRow, JudgeProvider, MetricResult } from './types';
import { WorkIQClient } from './workiq-client';

export const SEMANTIC_RUBRIC_VERSION = 'evalscore-semantic-v1';

export interface JudgeScore {
  score: number;
  reason?: string;
  model?: string;
}

export interface Judge {
  provider: JudgeProvider;
  model?: string;
  score(row: EvalRow): Promise<JudgeScore>;
}

export function buildSemanticScoringPrompt(row: EvalRow, jsonResponse = false): string {
  const responseInstruction = jsonResponse
    ? 'Respond with strict JSON: {"score": number, "reason": "short rationale"}.'
    : 'Respond with ONLY a single number between 0 and 100, nothing else.';

  return [
    'Compare the following two answers for semantic similarity.',
    'Consider whether they convey the same meaning and information, even if worded differently.',
    'Rate the similarity on a scale from 0 to 100, where 0 means completely different and 100 means identical in meaning.',
    responseInstruction,
    '',
    `Expected Answer: ${row.expectedAnswer}`,
    '',
    `Actual Answer: ${row.actualAnswer}`,
  ].join('\n');
}

export function parseJudgeScore(response: string): JudgeScore {
  const trimmed = response.trim();
  if (trimmed.startsWith('{')) {
    try {
      const parsed = JSON.parse(trimmed) as { score?: unknown; reason?: unknown; rationale?: unknown; model?: unknown };
      if (typeof parsed.score === 'number' && Number.isFinite(parsed.score)) {
        return {
          score: clampScore(parsed.score),
          reason: typeof parsed.reason === 'string' ? parsed.reason : toOptionalString(parsed.rationale),
          model: toOptionalString(parsed.model),
        };
      }
    } catch {
      // Fall through to numeric parsing for providers that wrap JSON in text.
    }
  }

  const match = trimmed.match(/\d+/);
  if (!match) {
    throw new Error(`Could not parse score from judge response: ${trimmed.slice(0, 120)}`);
  }

  return { score: clampScore(Number.parseInt(match[0], 10)) };
}

export class WorkIQJudge implements Judge {
  provider: JudgeProvider = 'workiq';

  constructor(private client: WorkIQClient, private tenantId?: string) {}

  async score(row: EvalRow): Promise<JudgeScore> {
    const prompt = buildSemanticScoringPrompt(row);
    const response = await this.client.ask(prompt, this.tenantId);
    return parseJudgeScore(response);
  }
}

export class GitHubCopilotJudge implements Judge {
  provider: JudgeProvider = 'github-copilot';
  model = process.env.EVALSCORE_GITHUB_COPILOT_MODEL;

  async score(row: EvalRow): Promise<JudgeScore> {
    const command = process.env.EVALSCORE_GITHUB_COPILOT_COMMAND;
    if (!command) {
      throw new Error(
        'GitHub Copilot judging requires EVALSCORE_GITHUB_COPILOT_COMMAND. ' +
        'The command must read the rubric prompt from stdin and return JSON or a 0-100 score.'
      );
    }

    const response = await runPromptCommand(command, buildSemanticScoringPrompt(row, true));
    const parsed = parseJudgeScore(response);
    return { ...parsed, model: parsed.model ?? this.model };
  }
}

export class AzureOpenAIJudge implements Judge {
  provider: JudgeProvider = 'azure-openai';
  model: string;
  private endpoint: string;
  private apiKey: string;
  private apiVersion: string;

  constructor() {
    this.endpoint = (process.env.AZURE_OPENAI_ENDPOINT ?? process.env.AZURE_AI_OPENAI_ENDPOINT ?? '').replace(/\/+$/, '');
    this.apiKey = process.env.AZURE_OPENAI_API_KEY ?? process.env.AZURE_AI_API_KEY ?? '';
    this.apiVersion = process.env.AZURE_OPENAI_API_VERSION ?? process.env.AZURE_AI_API_VERSION ?? '';
    this.model = process.env.AZURE_OPENAI_DEPLOYMENT ?? process.env.AZURE_AI_MODEL_NAME ?? '';
  }

  async score(row: EvalRow): Promise<JudgeScore> {
    if (!this.endpoint || !this.apiKey || !this.apiVersion || !this.model) {
      throw new Error(
        'Azure OpenAI judging requires AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, ' +
        'AZURE_OPENAI_API_VERSION, and AZURE_OPENAI_DEPLOYMENT.'
      );
    }

    const url = `${this.endpoint}/openai/deployments/${encodeURIComponent(this.model)}/chat/completions?api-version=${encodeURIComponent(this.apiVersion)}`;
    const response = await fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'api-key': this.apiKey,
      },
      body: JSON.stringify({
        temperature: 0,
        messages: [
          { role: 'system', content: 'You are a strict evaluation judge. Return only valid JSON.' },
          { role: 'user', content: buildSemanticScoringPrompt(row, true) },
        ],
      }),
    });

    if (!response.ok) {
      const retryAfter = response.headers.get('retry-after');
      const body = await response.text().catch(() => '');
      throw new Error(`Azure OpenAI HTTP ${response.status}${retryAfter ? ` retry-after=${retryAfter}` : ''}: ${body}`.trim());
    }

    const raw = await response.json() as { choices?: Array<{ message?: { content?: string } }> };
    const content = raw.choices?.[0]?.message?.content;
    if (!content) {
      throw new Error('Azure OpenAI returned an empty judge response.');
    }

    const parsed = parseJudgeScore(content);
    return { ...parsed, model: parsed.model ?? this.model };
  }
}

export function createJudge(provider: JudgeProvider, client: WorkIQClient, tenantId?: string): Judge {
  switch (provider) {
    case 'workiq':
      return new WorkIQJudge(client, tenantId);
    case 'github-copilot':
      return new GitHubCopilotJudge();
    case 'azure-openai':
      return new AzureOpenAIJudge();
  }
}

export function metricFromJudge(score: JudgeScore, judge: Judge, threshold?: number): MetricResult {
  return {
    name: 'SemanticSimilarity',
    score: score.score,
    passed: threshold === undefined ? undefined : score.score >= threshold,
    reason: score.reason,
    provider: judge.provider,
    model: score.model ?? judge.model,
    scale: '0-100',
    rubricVersion: SEMANTIC_RUBRIC_VERSION,
    threshold,
  };
}

function clampScore(score: number): number {
  return Math.max(0, Math.min(100, Math.round(score)));
}

function runPromptCommand(command: string, prompt: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const child = spawn(command, { shell: true, stdio: ['pipe', 'pipe', 'pipe'] });
    const stdout: Buffer[] = [];
    const stderr: Buffer[] = [];

    child.stdout.on('data', data => stdout.push(Buffer.from(data)));
    child.stderr.on('data', data => stderr.push(Buffer.from(data)));
    child.on('error', reject);
    child.on('close', code => {
      const output = Buffer.concat(stdout).toString('utf-8');
      if (code === 0) {
        resolve(output);
        return;
      }
      reject(new Error(`GitHub Copilot judge command exited with code ${code}: ${Buffer.concat(stderr).toString('utf-8')}`));
    });

    child.stdin.end(prompt);
  });
}

function toOptionalString(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}
