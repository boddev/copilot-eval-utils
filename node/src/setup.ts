import * as fs from 'fs';
import * as path from 'path';
import * as readline from 'readline';

const EULA_MARKER_FILE = path.join(
  process.env.USERPROFILE || process.env.HOME || '.',
  '.workiq-eula-accepted'
);
const EULA_URL = 'https://github.com/microsoft/work-iq-mcp';

export interface PreflightResult {
  passed: boolean;
  checks: {
    name: string;
    passed: boolean;
    message: string;
  }[];
}

export interface ConnectivityResult {
  connected: boolean;
  message: string;
  responseTimeMs: number;
}

export interface RunPreflightOptions {
  tenantId?: string;
  skipConnectivityTest?: boolean;
  askClient?: (question: string) => Promise<string>;
}

/**
 * Check if the WorkIQ EULA has been accepted (marker file exists).
 */
export function checkEulaAccepted(): boolean {
  return fs.existsSync(EULA_MARKER_FILE);
}

/**
 * Record EULA acceptance by writing a marker file.
 */
export function recordEulaAcceptance(): void {
  const dir = path.dirname(EULA_MARKER_FILE);
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(
    EULA_MARKER_FILE,
    `Accepted on ${new Date().toISOString()} for ${EULA_URL}\n`,
    'utf-8'
  );
}

/**
 * Get the EULA URL for display purposes.
 */
export function getEulaUrl(): string {
  return EULA_URL;
}

/**
 * Interactively prompt the user to accept the WorkIQ EULA.
 * Returns true if accepted, false if declined.
 */
export async function approveEula(): Promise<boolean> {
  console.error('');
  console.error('┌──────────────────────────────────────────────────┐');
  console.error('│  WorkIQ End User License Agreement               │');
  console.error('└──────────────────────────────────────────────────┘');
  console.error('');
  console.error('  Before using WorkIQ, you must accept the EULA.');
  console.error(`  Review the terms at: ${EULA_URL}`);
  console.error('');

  const rl = readline.createInterface({ input: process.stdin, output: process.stderr });

  const answer = await new Promise<string>((resolve) => {
    rl.question('  Do you accept the WorkIQ EULA? (yes/no): ', (response) => {
      rl.close();
      resolve(response.trim().toLowerCase());
    });
  });

  if (answer === 'y' || answer === 'yes') {
    recordEulaAcceptance();
    console.error('  ✅ EULA accepted.');
    return true;
  } else {
    console.error('  ❌ EULA declined. WorkIQ cannot be used without accepting the EULA.');
    return false;
  }
}

/**
 * Test WorkIQ connectivity by sending a lightweight test prompt.
 * Accepts an optional askClient for testing purposes.
 */
export async function testConnectivity(
  tenantId?: string,
  askClient?: (question: string) => Promise<string>
): Promise<ConnectivityResult> {
  const start = Date.now();

  try {
    if (askClient) {
      await askClient("Reply with the word 'connected' to confirm you are working.");
    } else {
      // Import lazily to avoid circular dependency issues at module load time
      const { CliWorkIQClient } = await import('./workiq-client');
      const client = new CliWorkIQClient();
      await client.start(tenantId);
      try {
        await client.ask('Say hello', tenantId);
      } finally {
        client.stop();
      }
    }

    const responseTimeMs = Date.now() - start;
    return {
      connected: true,
      message: `WorkIQ responded in ${(responseTimeMs / 1000).toFixed(1)}s`,
      responseTimeMs,
    };
  } catch (err: unknown) {
    const responseTimeMs = Date.now() - start;
    const message = err instanceof Error ? err.message : String(err);
    return {
      connected: false,
      message:
        `WorkIQ connectivity test failed: ${message}\n` +
        `        Verify workiq works by running: workiq${tenantId ? ` -t ${tenantId}` : ''} ask -q "Say hello"`,
      responseTimeMs,
    };
  }
}

/**
 * Run all preflight checks: EULA acceptance and optionally WorkIQ connectivity.
 * If EULA is not yet accepted, prompts the user interactively.
 * Authentication is handled by the WorkIQ CLI and M365 Copilot locally.
 */
export async function runPreflight(options: RunPreflightOptions = {}): Promise<PreflightResult> {
  const { tenantId, skipConnectivityTest = true, askClient } = options;
  const checks: PreflightResult['checks'] = [];

  // 1. WorkIQ EULA
  let eulaAccepted = checkEulaAccepted();
  if (!eulaAccepted) {
    eulaAccepted = await approveEula();
  }
  checks.push({
    name: 'WorkIQ EULA',
    passed: eulaAccepted,
    message: eulaAccepted ? 'EULA accepted' : 'EULA declined',
  });

  // 2. Connectivity test (if not skipped)
  if (!skipConnectivityTest) {
    const connResult = await testConnectivity(tenantId, askClient);
    checks.push({
      name: 'WorkIQ connectivity',
      passed: connResult.connected,
      message: connResult.message,
    });
  } else {
    checks.push({
      name: 'WorkIQ connectivity',
      passed: true,
      message: 'Skipped',
    });
  }

  return {
    passed: checks.every((c) => c.passed),
    checks,
  };
}

/**
 * Print preflight results to stderr.
 */
export function printPreflightResults(result: PreflightResult): void {
  console.error('');
  console.error('╔══════════════════════════════════════════════╗');
  console.error('║  Preflight Checks                            ║');
  console.error('╚══════════════════════════════════════════════╝');
  console.error('');

  let displayIndex = 0;

  for (const check of result.checks) {
    if (check.message === 'Skipped') {
      displayIndex++;
      console.error(`  [${displayIndex}/${result.checks.length}] ${check.name}... ⏭️  Skipped`);
      continue;
    }
    displayIndex++;
    const icon = check.passed ? '✅' : '❌';
    console.error(`  [${displayIndex}/${result.checks.length}] ${check.name}... ${icon} ${check.message}`);
  }

  console.error('');

  if (!result.passed) {
    console.error('  ──────────────────────────────────────────');
    console.error('  One or more preflight checks failed.');
    console.error('  Fix the issues above and try again.');
    console.error('  ──────────────────────────────────────────');
    console.error('');
  } else {
    console.error('  All preflight checks passed.');
    console.error('');
  }
}
