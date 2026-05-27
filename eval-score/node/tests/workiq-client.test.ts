import { afterEach, describe, expect, it, vi } from 'vitest';
import { A2AWorkIQClient, isRetryableWorkIQError, looksLikeRateLimitText } from '../src/workiq-client';

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllEnvs();
});

describe('isRetryableWorkIQError', () => {
  it('retries transient WorkIQ transport failures', () => {
    expect(isRetryableWorkIQError(new Error('Timed out waiting for MCP response'))).toBe(true);
    expect(isRetryableWorkIQError(new Error('WorkIQ MCP process exited with code 1'))).toBe(true);
    expect(isRetryableWorkIQError(new Error('HTTP 429 rate limit'))).toBe(true);
    expect(isRetryableWorkIQError(new Error('HTTP 503 temporarily unavailable'))).toBe(true);
  });

  describe('A2AWorkIQClient', () => {
    it('requires A2A endpoint for agent targeting', async () => {
      const client = new A2AWorkIQClient({ endpoint: '', accessToken: '', tokenCommand: '', authMode: 'auto' });

      await expect(client.start()).rejects.toThrow(/WORK_IQ_A2A_ENDPOINT/);
    });

    it('requires a token source for A2A agent targeting', async () => {
      const client = new A2AWorkIQClient({ endpoint: 'https://a2a.example.test', accessToken: '', tokenCommand: '', authMode: 'auto' });

      await expect(client.start()).rejects.toThrow(/WORK_IQ_A2A_ACCESS_TOKEN|WORK_IQ_A2A_TOKEN_COMMAND|EVALSCORE_A2A_AUTH_MODE=msal/);
    });

    it('accepts an injected token provider for A2A agent targeting', async () => {
      const client = new A2AWorkIQClient({
        endpoint: 'https://a2a.example.test',
        accessToken: '',
        tokenCommand: '',
        authMode: 'auto',
        tokenProvider: { getToken: async () => 'token' },
      });

      await expect(client.start()).resolves.toBeUndefined();
    });

    it('validates required MSAL settings when MSAL auth is explicitly selected', async () => {
      vi.stubEnv('EVALSCORE_A2A_CLIENT_ID', '');
      vi.stubEnv('WORK_IQ_A2A_CLIENT_ID', '');
      vi.stubEnv('EVALSCORE_A2A_TENANT_ID', '');
      vi.stubEnv('WORK_IQ_A2A_TENANT_ID', '');
      vi.stubEnv('TENANT_ID', '');
      vi.stubEnv('EVALSCORE_A2A_SCOPES', '');
      vi.stubEnv('WORK_IQ_A2A_SCOPES', '');

      const client = new A2AWorkIQClient({
        endpoint: 'https://a2a.example.test',
        accessToken: '',
        tokenCommand: '',
        authMode: 'msal',
      });

      await expect(client.start()).rejects.toThrow(/MSAL A2A auth requires client ID, tenant ID, scopes/);
    });

    it('forces token refresh before retrying a 401 A2A request', async () => {
      const forceRefreshValues: boolean[] = [];
      const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        if (url.endsWith('/.agents') || url.endsWith('/.well-known/agent-card.json')) {
          return new Response('', { status: 404 });
        }
        if (init?.method === 'POST' && fetchMock.mock.calls.filter(call => call[1]?.method === 'POST').length === 1) {
          return new Response('unauthorized', { status: 401 });
        }
        return Response.json({
          result: {
            kind: 'message',
            parts: [{ kind: 'text', text: 'answer' }],
            contextId: 'conversation-1',
          },
        });
      });
      vi.stubGlobal('fetch', fetchMock);

      const client = new A2AWorkIQClient({
        endpoint: 'https://a2a.example.test',
        accessToken: '',
        tokenCommand: '',
        authMode: 'auto',
        tokenProvider: {
          getToken: async (forceRefresh = false) => {
            forceRefreshValues.push(forceRefresh);
            return forceRefresh ? 'fresh-token' : 'stale-token';
          },
        },
      });

      const response = await client.askWithMetadata('question', { agentId: 'agent-1' });

      expect(response.text).toBe('answer');
      expect(forceRefreshValues).toContain(true);
    });
  });

  it('does not retry authentication or EULA failures', () => {
    expect(isRetryableWorkIQError(new Error('401 unauthorized'))).toBe(false);
    expect(isRetryableWorkIQError(new Error('403 forbidden'))).toBe(false);
    expect(isRetryableWorkIQError(new Error('WorkIQ EULA must be accepted'))).toBe(false);
  });

  describe('looksLikeRateLimitText', () => {
    it('detects the Work IQ hourly rate-limit apology', () => {
      expect(
        looksLikeRateLimitText(
          "You've reached the limit on the number of requests per hour. Try again in a little while."
        )
      ).toBe(true);
      expect(looksLikeRateLimitText('Too many requests, please slow down.')).toBe(true);
      expect(looksLikeRateLimitText('You are being rate-limited.')).toBe(true);
    });

    it('does not flag normal agent responses as rate-limited', () => {
      expect(looksLikeRateLimitText('Here are the CMS dialysis state averages...')).toBe(false);
      expect(looksLikeRateLimitText('')).toBe(false);
      expect(looksLikeRateLimitText(undefined)).toBe(false);
    });
  });

  it('treats a Work IQ rate-limit body as a retryable 429', async () => {
    const responses = [
      Response.json({
        result: {
          kind: 'message',
          parts: [
            {
              kind: 'text',
              text: "You've reached the limit on the number of requests per hour. Try again in a little while.",
            },
          ],
        },
      }),
      Response.json({
        result: {
          kind: 'message',
          parts: [{ kind: 'text', text: 'final answer' }],
          contextId: 'ctx-1',
        },
      }),
    ];
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.endsWith('/.agents') || url.endsWith('/.well-known/agent-card.json')) {
        return new Response('', { status: 404 });
      }
      if (init?.method === 'POST') {
        return responses.shift() ?? new Response('exhausted', { status: 500 });
      }
      return new Response('', { status: 404 });
    });
    vi.stubGlobal('fetch', fetchMock);

    const client = new A2AWorkIQClient({
      endpoint: 'https://a2a.example.test',
      accessToken: 'token',
      tokenCommand: '',
      authMode: 'auto',
      maxAttempts: 3,
      backoffBaseMs: 1,
    });

    const response = await client.askWithMetadata('question', { agentId: 'agent-1' });

    expect(response.text).toBe('final answer');
    const postCalls = fetchMock.mock.calls.filter((call) => (call[1] as RequestInit | undefined)?.method === 'POST');
    expect(postCalls.length).toBeGreaterThanOrEqual(2);
  });
});
