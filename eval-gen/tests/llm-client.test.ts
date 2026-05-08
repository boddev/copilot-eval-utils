import { describe, expect, it } from 'vitest';
import {
  createLLMClient,
  isRetryableA2AError,
  isRetryableCopilotApiError,
  isRetryableWorkIQError,
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

describe('isRetryableWorkIQError', () => {
  it('treats MCP timeouts as retryable', () => {
    expect(isRetryableWorkIQError(new Error('Timed out waiting for WorkIQ MCP response (id=4)'))).toBe(true);
  });

  it('treats broken pipes and dead processes as retryable', () => {
    expect(isRetryableWorkIQError(new Error('WorkIQ MCP process is not running'))).toBe(true);
    expect(isRetryableWorkIQError(new Error('write EPIPE'))).toBe(true);
    expect(isRetryableWorkIQError(new Error('socket hang up'))).toBe(true);
  });

  it('treats throttling and transient HTTP errors as retryable', () => {
    expect(isRetryableWorkIQError(new Error('HTTP 429 Too Many Requests'))).toBe(true);
    expect(isRetryableWorkIQError(new Error('Service returned 503'))).toBe(true);
    expect(isRetryableWorkIQError(new Error('Rate limit exceeded'))).toBe(true);
  });

  it('does not retry auth or EULA failures', () => {
    expect(isRetryableWorkIQError(new Error('EULA not accepted'))).toBe(false);
    expect(isRetryableWorkIQError(new Error('401 Unauthorized'))).toBe(false);
    expect(isRetryableWorkIQError(new Error('403 Forbidden'))).toBe(false);
  });

  it('does not retry non-Error throwables', () => {
    expect(isRetryableWorkIQError('string error')).toBe(false);
    expect(isRetryableWorkIQError(null)).toBe(false);
  });
});

describe('isRetryableCopilotApiError', () => {
  it('treats network errors and timeouts as retryable', () => {
    expect(isRetryableCopilotApiError(new Error('fetch failed'))).toBe(true);
    expect(isRetryableCopilotApiError(new Error('ETIMEDOUT'))).toBe(true);
    expect(isRetryableCopilotApiError(new Error('socket hang up'))).toBe(true);
  });

  it('retries empty/no-message responses (likely transient)', () => {
    expect(isRetryableCopilotApiError(new Error('Microsoft 365 Copilot Chat API did not return a conversation id'))).toBe(true);
    expect(isRetryableCopilotApiError(new Error('Microsoft 365 Copilot Chat API returned no message text'))).toBe(true);
  });

  it('does not retry random non-network errors', () => {
    expect(isRetryableCopilotApiError(new Error('JSON.parse failed'))).toBe(false);
    expect(isRetryableCopilotApiError('string error')).toBe(false);
  });
});

describe('isRetryableA2AError', () => {
  it('retries network errors and timeouts', () => {
    expect(isRetryableA2AError(new Error('Work IQ A2A request timed out after 300000 ms'))).toBe(true);
    expect(isRetryableA2AError(new Error('fetch failed'))).toBe(true);
    expect(isRetryableA2AError(new Error('ECONNRESET'))).toBe(true);
  });

  it('retries empty/missing-task responses', () => {
    expect(isRetryableA2AError(new Error('Work IQ A2A response is missing result.task'))).toBe(true);
    expect(isRetryableA2AError(new Error('Work IQ A2A task completed but contained no text artifact'))).toBe(true);
  });

  it('does not retry random unrelated errors', () => {
    expect(isRetryableA2AError(new Error('JSON.parse failed'))).toBe(false);
    expect(isRetryableA2AError('not an error')).toBe(false);
  });
});
