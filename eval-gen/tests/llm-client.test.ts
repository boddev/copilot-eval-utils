import { describe, expect, it } from 'vitest';
import {
  createLLMClient,
  Microsoft365CopilotChatClient,
  parseStructuredJson,
  WorkIQCopilotClient,
} from '../src/llm-client';

describe('parseStructuredJson', () => {
  it('parses a raw JSON object', () => {
    expect(parseStructuredJson<{ value: number }>('{"value":42}')).toEqual({ value: 42 });
  });

  it('parses JSON from a fenced code block', () => {
    const output = 'Here is the JSON:\n```json\n{"items":["a","b"]}\n```';
    expect(parseStructuredJson<{ items: string[] }>(output)).toEqual({ items: ['a', 'b'] });
  });

  it('parses JSON embedded in provider prose', () => {
    const output = 'Done.\n{"questions":[{"prompt":"Q?","expected_answer":"A"}]}\nThanks.';
    expect(parseStructuredJson<{ questions: unknown[] }>(output).questions).toHaveLength(1);
  });
});

describe('createLLMClient', () => {
  it('defaults to Microsoft 365 Copilot', () => {
    const previousProvider = process.env.EVALGEN_PROVIDER;
    delete process.env.EVALGEN_PROVIDER;
    try {
      expect(createLLMClient()).toBeInstanceOf(WorkIQCopilotClient);
    } finally {
      if (previousProvider === undefined) {
        delete process.env.EVALGEN_PROVIDER;
      } else {
        process.env.EVALGEN_PROVIDER = previousProvider;
      }
    }
  });

  it('keeps direct Graph API provider available explicitly', () => {
    expect(createLLMClient({ provider: 'm365-copilot-api' })).toBeInstanceOf(Microsoft365CopilotChatClient);
  });
});
