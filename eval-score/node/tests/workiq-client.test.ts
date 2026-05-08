import { describe, expect, it } from 'vitest';
import { isRetryableWorkIQError } from '../src/workiq-client';

describe('isRetryableWorkIQError', () => {
  it('retries transient WorkIQ transport failures', () => {
    expect(isRetryableWorkIQError(new Error('Timed out waiting for MCP response'))).toBe(true);
    expect(isRetryableWorkIQError(new Error('WorkIQ MCP process exited with code 1'))).toBe(true);
    expect(isRetryableWorkIQError(new Error('HTTP 429 rate limit'))).toBe(true);
    expect(isRetryableWorkIQError(new Error('HTTP 503 temporarily unavailable'))).toBe(true);
  });

  it('does not retry authentication or EULA failures', () => {
    expect(isRetryableWorkIQError(new Error('401 unauthorized'))).toBe(false);
    expect(isRetryableWorkIQError(new Error('403 forbidden'))).toBe(false);
    expect(isRetryableWorkIQError(new Error('WorkIQ EULA must be accepted'))).toBe(false);
  });
});
