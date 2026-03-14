import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';

vi.mock('fs', async (importOriginal) => {
  const actual = await importOriginal<typeof import('fs')>();
  return { ...actual };
});

describe('checkEulaAccepted', () => {
  it('returns false when marker file does not exist', async () => {
    const { checkEulaAccepted } = await import('../src/setup');
    // Use a path that does not exist
    vi.spyOn(fs, 'existsSync').mockReturnValueOnce(false);
    expect(checkEulaAccepted()).toBe(false);
  });

  it('returns true when marker file exists', async () => {
    const { checkEulaAccepted } = await import('../src/setup');
    vi.spyOn(fs, 'existsSync').mockReturnValueOnce(true);
    expect(checkEulaAccepted()).toBe(true);
  });
});

describe('recordEulaAcceptance', () => {
  it('writes the marker file', async () => {
    const { recordEulaAcceptance } = await import('../src/setup');
    const mkdirSpy = vi.spyOn(fs, 'mkdirSync').mockImplementation(() => undefined);
    const writeSpy = vi.spyOn(fs, 'writeFileSync').mockImplementation(() => undefined);

    recordEulaAcceptance();

    expect(mkdirSpy).toHaveBeenCalled();
    expect(writeSpy).toHaveBeenCalled();
    const writeArgs = writeSpy.mock.calls[0];
    expect(writeArgs[1] as string).toContain('Accepted on');
    expect(writeArgs[1] as string).toContain('https://github.com/microsoft/work-iq-mcp');
  });
});

describe('testConnectivity', () => {
  it('returns connected=true when askClient succeeds', async () => {
    const { testConnectivity } = await import('../src/setup');
    const mockAsk = vi.fn().mockResolvedValue('connected');

    const result = await testConnectivity(undefined, mockAsk);

    expect(result.connected).toBe(true);
    expect(result.message).toContain('WorkIQ responded');
    expect(result.responseTimeMs).toBeGreaterThanOrEqual(0);
    expect(mockAsk).toHaveBeenCalledOnce();
  });

  it('returns connected=false when askClient throws', async () => {
    const { testConnectivity } = await import('../src/setup');
    const mockAsk = vi.fn().mockRejectedValue(new Error('Connection refused'));

    const result = await testConnectivity(undefined, mockAsk);

    expect(result.connected).toBe(false);
    expect(result.message).toContain('WorkIQ connectivity test failed');
    expect(result.message).toContain('Connection refused');
  });
});

describe('runPreflight', () => {
  it('passes when EULA is already accepted and connectivity test succeeds', async () => {
    const { runPreflight } = await import('../src/setup');
    vi.spyOn(fs, 'existsSync').mockReturnValue(true);
    const mockAsk = vi.fn().mockResolvedValue('connected');

    const result = await runPreflight({ skipConnectivityTest: false, askClient: mockAsk });

    expect(result.passed).toBe(true);
    expect(result.checks).toHaveLength(2);
    expect(result.checks[0].name).toBe('WorkIQ EULA');
    expect(result.checks[0].passed).toBe(true);
    expect(result.checks[1].name).toBe('WorkIQ connectivity');
    expect(result.checks[1].passed).toBe(true);
  });

  it('fails when EULA accepted but connectivity test fails', async () => {
    const { runPreflight } = await import('../src/setup');
    vi.spyOn(fs, 'existsSync').mockReturnValue(true);
    const mockAsk = vi.fn().mockRejectedValue(new Error('Timeout'));

    const result = await runPreflight({ skipConnectivityTest: false, askClient: mockAsk });

    expect(result.passed).toBe(false);
    expect(result.checks[1].passed).toBe(false);
    expect(result.checks[1].message).toContain('Timeout');
  });

  it('skips connectivity test when skipConnectivityTest=true', async () => {
    const { runPreflight } = await import('../src/setup');
    vi.spyOn(fs, 'existsSync').mockReturnValue(true);
    const mockAsk = vi.fn();

    const result = await runPreflight({ skipConnectivityTest: true, askClient: mockAsk });

    expect(result.passed).toBe(true);
    expect(result.checks[1].message).toBe('Skipped');
    expect(mockAsk).not.toHaveBeenCalled();
  });

  it('defaults to skipping connectivity test', async () => {
    const { runPreflight } = await import('../src/setup');
    vi.spyOn(fs, 'existsSync').mockReturnValue(true);
    const mockAsk = vi.fn();

    const result = await runPreflight({ askClient: mockAsk });

    expect(result.checks[1].message).toBe('Skipped');
    expect(mockAsk).not.toHaveBeenCalled();
  });
});
