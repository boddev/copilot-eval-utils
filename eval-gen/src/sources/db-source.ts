import { DataSourceAdapter, SourceResult } from './types';

interface DatabaseSourceOptions {
  /** Database type */
  type: 'sqlite' | 'postgresql' | 'mssql';
  /** Connection string or file path (for SQLite) */
  connectionString: string;
  /** Specific tables to sample (if empty, discovers all) */
  tables?: string[];
  /** Max rows to sample per table */
  maxRowsPerTable?: number;
}

/**
 * Database data source adapter.
 * Reads schema metadata and samples rows from tables.
 *
 * Note: This is a lightweight implementation using child_process to call
 * database CLI tools (sqlite3, psql, sqlcmd). For production use,
 * consider using proper database driver packages.
 */
export class DatabaseSource implements DataSourceAdapter {
  private options: DatabaseSourceOptions;

  constructor(options: DatabaseSourceOptions) {
    this.options = {
      maxRowsPerTable: 100,
      ...options,
    };
  }

  async fetch(): Promise<SourceResult> {
    switch (this.options.type) {
      case 'sqlite':
        return this.fetchSqlite();
      default:
        throw new Error(
          `Database type "${this.options.type}" is not yet implemented. ` +
          `Currently supported: sqlite. Export your data as CSV/JSON and use --file instead.`
        );
    }
  }

  private async fetchSqlite(): Promise<SourceResult> {
    // Dynamic import to avoid requiring better-sqlite3 as a hard dependency
    let Database: any;
    try {
      Database = require('better-sqlite3');
    } catch {
      throw new Error(
        'SQLite support requires the "better-sqlite3" package. ' +
        'Install it with: npm install better-sqlite3\n' +
        'Or export your data as CSV/JSON and use --file instead.'
      );
    }

    const db = new Database(this.options.connectionString, { readonly: true });

    try {
      // Discover tables
      const tables: string[] = this.options.tables ?? db
        .prepare("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")
        .all()
        .map((row: { name: string }) => row.name);

      if (tables.length === 0) {
        throw new Error('No tables found in database');
      }

      const allRecords: Record<string, unknown>[] = [];

      for (const table of tables) {
        const rows = db
          .prepare(`SELECT * FROM "${table}" LIMIT ${this.options.maxRowsPerTable}`)
          .all() as Record<string, unknown>[];

        // Add _source_table metadata so we know where records came from
        for (const row of rows) {
          row._source_table = table;
        }

        allRecords.push(...rows);
      }

      return {
        records: allRecords,
        format: 'json',
        sourceName: `sqlite:${this.options.connectionString}`,
      };
    } finally {
      db.close();
    }
  }
}
