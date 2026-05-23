import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import { EvalRow } from '../src/types';
import { evaluatePrompts } from '../src/evaluator';
import { MockWorkIQClient, WorkIQClient } from '../src/workiq-client';

function makeRows(): EvalRow[] {
  return [
    {
      prompt: 'What is 2+2?',
      expectedAnswer: '4',
      sourceLocation: 'math.docx',
      actualAnswer: '',
    },
    {
      prompt: 'Capital of France?',
      expectedAnswer: 'Paris',
      sourceLocation: 'geo.xlsx',
      actualAnswer: '',
    },
  ];
}

describe('evaluatePrompts', () => {
  it('populates actualAnswer from the client response', async () => {
    const client = new MockWorkIQClient({}, 'Mock answer');
    const rows = makeRows();

    const result = await evaluatePrompts(rows, client);

    expect(result).toHaveLength(2);
    expect(result[0].actualAnswer).toBe('Mock answer');
    expect(result[1].actualAnswer).toBe('Mock answer');
  });

  it('skips rows that already have an actualAnswer', async () => {
    const callLog: string[] = [];
    const client: WorkIQClient = {
      async ask(question: string, tenantId?: string) {
        callLog.push(question);
        return 'New answer';
      },
    };

    const rows: EvalRow[] = [
      {
        prompt: 'Already answered',
        expectedAnswer: 'Expected',
        sourceLocation: 'loc',
        actualAnswer: 'Pre-existing answer',
      },
      {
        prompt: 'Needs answer',
        expectedAnswer: 'Expected',
        sourceLocation: 'loc',
        actualAnswer: '',
      },
    ];

    await evaluatePrompts(rows, client);

    expect(rows[0].actualAnswer).toBe('Pre-existing answer');
    expect(rows[1].actualAnswer).toBe('New answer');
    expect(callLog).toHaveLength(1);
    expect(callLog[0]).toBe('Needs answer');
  });

  it('captures errors as [ERROR: ...] in actualAnswer', async () => {
    const client: WorkIQClient = {
      async ask(tenantId?: string) {
        throw new Error('Network timeout');
      },
    };
    const rows = makeRows();

    await evaluatePrompts(rows, client);

    expect(rows[0].actualAnswer).toMatch(/^\[ERROR:/);
    expect(rows[0].actualAnswer).toContain('Network timeout');
  });

  it('calls onProgress with correct counts', async () => {
    const client = new MockWorkIQClient({}, 'answer');
    const rows = makeRows();
    const progressCalls: Array<{ completed: number; total: number; prompt: string }> = [];

    await evaluatePrompts(rows, client, {
      onProgress: (completed, total, currentPrompt) => {
        progressCalls.push({ completed, total, prompt: currentPrompt });
      },
    });

    expect(progressCalls).toHaveLength(2);
    expect(progressCalls[0]).toEqual({ completed: 1, total: 2, prompt: 'What is 2+2?' });
    expect(progressCalls[1]).toEqual({ completed: 2, total: 2, prompt: 'Capital of France?' });
  });

  it('prepends connector targeting instructions when connectorPromptHint is enabled', async () => {
    const callLog: string[] = [];
    const client: WorkIQClient = {
      async ask(question: string) {
        callLog.push(question);
        return 'answer';
      },
    };
    const rows = [makeRows()[0]];

    await evaluatePrompts(rows, client, {
      connectorId: 'ngoenvironment',
      connectorPromptHint: true,
      systemPrompt: 'Use environmental data.',
    });

    expect(callLog).toHaveLength(1);
    expect(callLog[0]).toContain('Target Microsoft 365 Copilot connector ID: ngoenvironment');
    expect(callLog[0]).toContain('Use environmental data.');
    expect(callLog[0]).toContain('What is 2+2?');
  });

  it('does not inject connector targeting instructions by default', async () => {
    const callLog: string[] = [];
    const client: WorkIQClient = {
      async ask(question: string) {
        callLog.push(question);
        return 'answer';
      },
    };
    const rows = [makeRows()[0]];

    await evaluatePrompts(rows, client, {
      connectorId: 'ngoenvironment',
      delayMs: 0,
    });

    expect(callLog).toEqual(['What is 2+2?']);
  });

  it('passes agent targeting options to metadata-aware clients', async () => {
    const callLog: Array<{ question: string; agentId?: string }> = [];
    const client: WorkIQClient = {
      async ask() {
        throw new Error('ask should not be called');
      },
      async askWithMetadata(question, options) {
        callLog.push({ question, agentId: options?.agentId });
        return { text: 'agent answer', conversationId: 'ctx-1' };
      },
    };
    const rows = [makeRows()[0]];

    await evaluatePrompts(rows, client, {
      agentId: 'agent-123',
      delayMs: 0,
    });

    expect(callLog).toEqual([{ question: 'What is 2+2?', agentId: 'agent-123' }]);
    expect(rows[0].actualAnswer).toBe('agent answer');
    expect(rows[0].conversationId).toBe('ctx-1');
  });
});
