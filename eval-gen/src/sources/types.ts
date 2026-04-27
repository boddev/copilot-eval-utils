import { InputFormat } from '../types';

/**
 * Common result from any data source adapter
 */
export interface SourceResult {
  records: Record<string, unknown>[];
  format: InputFormat;
  sourceName: string;
}

/**
 * Interface for data source adapters
 */
export interface DataSourceAdapter {
  /** Connect and fetch records from the source */
  fetch(): Promise<SourceResult>;
}
