import { describe, expect, it } from 'vitest';
import { A2AWorkIQClient, isRetryableWorkIQError } from '../src/workiq-client';

describe('isRetryableWorkIQError', () => {
  it('retries transient WorkIQ transport failures', () => {
    expect(isRetryableWorkIQError(new Error('Timed out waiting for MCP response'))).toBe(true);
    expect(isRetryableWorkIQError(new Error('WorkIQ MCP process exited with code 1'))).toBe(true);
    expect(isRetryableWorkIQError(new Error('HTTP 429 rate limit'))).toBe(true);
    expect(isRetryableWorkIQError(new Error('HTTP 503 temporarily unavailable'))).toBe(true);
  });

  describe('A2AWorkIQClient', () => {
    it('requires A2A endpoint and access token for agent targeting', async () => {
      const client = new A2AWorkIQClient({ endpoint: '', accessToken: '' });

      await expect(client.start()).rejects.toThrow(/WORK_IQ_A2A_ENDPOINT/);
    });
  });

  it('does not retry authentication or EULA failures', () => {
    expect(isRetryableWorkIQError(new Error('401 unauthorized'))).toBe(false);
    expect(isRetryableWorkIQError(new Error('403 forbidden'))).toBe(false);
    expect(isRetryableWorkIQError(new Error('WorkIQ EULA must be accepted'))).toBe(false);
  });
});
