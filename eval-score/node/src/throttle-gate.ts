export class ThrottleGate {
  private active = 0;
  private queue: Array<() => void> = [];

  constructor(private maxConcurrent: number) {}

  async run<T>(operation: () => Promise<T>): Promise<T> {
    await this.acquire();
    try {
      return await operation();
    } finally {
      this.release();
    }
  }

  private acquire(): Promise<void> {
    if (this.active < this.maxConcurrent) {
      this.active++;
      return Promise.resolve();
    }
    return new Promise(resolve => {
      this.queue.push(() => {
        this.active++;
        resolve();
      });
    });
  }

  private release(): void {
    this.active--;
    const next = this.queue.shift();
    if (next) next();
  }
}

export function createThrottleGate(maxConcurrent?: number): ThrottleGate {
  const configured = maxConcurrent ?? Number.parseInt(process.env.EVALSCORE_MAX_CONCURRENCY ?? '5', 10);
  const safe = Number.isFinite(configured) && configured > 0 ? Math.min(configured, 5) : 5;
  return new ThrottleGate(safe);
}
