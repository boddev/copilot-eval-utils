import { DataSourceAdapter, SourceResult } from './types';

interface ApiSourceOptions {
  /** Base URL of the API */
  baseUrl: string;
  /** URL to OpenAPI/Swagger spec (optional but recommended) */
  specUrl?: string;
  /** Auth headers (e.g., { Authorization: 'Bearer xxx' }) */
  headers?: Record<string, string>;
  /** Specific endpoints to sample (if not using spec discovery) */
  endpoints?: string[];
  /** Max records to fetch per endpoint */
  maxRecordsPerEndpoint?: number;
}

interface OpenApiSpec {
  paths?: Record<string, Record<string, {
    summary?: string;
    responses?: Record<string, {
      content?: Record<string, { schema?: unknown }>;
    }>;
  }>>;
}

/**
 * API data source adapter.
 * Discovers endpoints via OpenAPI spec and samples data from GET endpoints.
 */
export class ApiSource implements DataSourceAdapter {
  private options: ApiSourceOptions;

  constructor(options: ApiSourceOptions) {
    this.options = {
      maxRecordsPerEndpoint: 50,
      ...options,
    };
  }

  async fetch(): Promise<SourceResult> {
    const endpoints = this.options.endpoints ?? await this.discoverEndpoints();

    if (endpoints.length === 0) {
      throw new Error('No API endpoints discovered. Provide --endpoints or a valid OpenAPI spec.');
    }

    const allRecords: Record<string, unknown>[] = [];

    for (const endpoint of endpoints) {
      try {
        const records = await this.fetchEndpoint(endpoint);
        allRecords.push(...records);
      } catch (err) {
        const msg = err instanceof Error ? err.message : String(err);
        process.stderr.write(`  Warning: Failed to fetch ${endpoint}: ${msg}\n`);
      }
    }

    if (allRecords.length === 0) {
      throw new Error('No data retrieved from any API endpoint');
    }

    return {
      records: allRecords,
      format: 'json',
      sourceName: new URL(this.options.baseUrl).hostname,
    };
  }

  /** Discover GET endpoints from OpenAPI spec */
  private async discoverEndpoints(): Promise<string[]> {
    if (!this.options.specUrl) {
      // Try common spec locations
      const commonPaths = ['/swagger.json', '/openapi.json', '/api-docs', '/v1/openapi.json'];
      for (const specPath of commonPaths) {
        try {
          const specUrl = `${this.options.baseUrl.replace(/\/$/, '')}${specPath}`;
          const response = await fetch(specUrl, { headers: this.options.headers });
          if (response.ok) {
            this.options.specUrl = specUrl;
            break;
          }
        } catch {
          continue;
        }
      }
    }

    if (!this.options.specUrl) {
      throw new Error('No OpenAPI spec found. Provide --openapi-spec or --endpoints.');
    }

    const response = await fetch(this.options.specUrl, { headers: this.options.headers });
    if (!response.ok) {
      throw new Error(`Failed to fetch OpenAPI spec: ${response.status}`);
    }

    const spec: OpenApiSpec = await response.json() as OpenApiSpec;
    const endpoints: string[] = [];

    if (spec.paths) {
      for (const [pathStr, methods] of Object.entries(spec.paths)) {
        // Only GET endpoints that look like list/collection endpoints
        if (methods.get && !pathStr.includes('{')) {
          endpoints.push(pathStr);
        }
      }
    }

    return endpoints.slice(0, 10); // Limit to 10 endpoints
  }

  /** Fetch data from a single endpoint */
  private async fetchEndpoint(endpoint: string): Promise<Record<string, unknown>[]> {
    const url = `${this.options.baseUrl.replace(/\/$/, '')}${endpoint}`;
    const response = await fetch(url, { headers: this.options.headers });

    if (!response.ok) {
      throw new Error(`${response.status} ${response.statusText}`);
    }

    const data = await response.json();

    // Handle different response shapes
    if (Array.isArray(data)) {
      return data.slice(0, this.options.maxRecordsPerEndpoint!) as Record<string, unknown>[];
    }

    // Try common wrapper patterns
    if (data && typeof data === 'object') {
      const obj = data as Record<string, unknown>;
      for (const key of ['data', 'results', 'items', 'records', 'value']) {
        if (Array.isArray(obj[key])) {
          return (obj[key] as Record<string, unknown>[]).slice(0, this.options.maxRecordsPerEndpoint!);
        }
      }
      // Single object response
      return [obj];
    }

    return [];
  }
}
