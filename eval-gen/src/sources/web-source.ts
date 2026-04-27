import { DataSourceAdapter, SourceResult } from './types';

interface WebSourceOptions {
  /** Starting URL to crawl */
  url: string;
  /** Maximum pages to crawl */
  maxPages?: number;
  /** Custom headers */
  headers?: Record<string, string>;
}

/**
 * Web data source adapter.
 * Crawls static web pages and extracts structured content.
 *
 * Note: Requires 'cheerio' package for HTML parsing.
 * Only supports static/server-rendered pages.
 */
export class WebSource implements DataSourceAdapter {
  private options: WebSourceOptions;

  constructor(options: WebSourceOptions) {
    this.options = {
      maxPages: 10,
      ...options,
    };
  }

  async fetch(): Promise<SourceResult> {
    let loadHtml: (html: string) => any;
    try {
      const cheerio = require('cheerio');
      loadHtml = cheerio.load;
    } catch {
      throw new Error(
        'Web source support requires the "cheerio" package. ' +
        'Install it with: npm install cheerio'
      );
    }

    const visited = new Set<string>();
    const records: Record<string, unknown>[] = [];
    const queue = [this.options.url];

    while (queue.length > 0 && visited.size < this.options.maxPages!) {
      const url = queue.shift()!;
      if (visited.has(url)) continue;
      visited.add(url);

      try {
        const response = await fetch(url, { headers: this.options.headers });
        if (!response.ok) continue;

        const contentType = response.headers.get('content-type') ?? '';
        if (!contentType.includes('text/html')) continue;

        const html = await response.text();
        const $ = loadHtml(html);

        // Extract page content
        const title = $('title').text().trim();
        const headings = $('h1, h2, h3').map((_: any, el: any) => $(el).text().trim()).get();
        const paragraphs = $('p').map((_: any, el: any) => $(el).text().trim()).get()
          .filter((p: string) => p.length > 20);

        // Extract tables if present
        let hasTableData = false;
        $('table').each((_: any, table: any) => {
          const tableHeaders: string[] = [];
          $(table).find('th').each((__: any, th: any) => tableHeaders.push($(th).text().trim()));

          if (tableHeaders.length > 0) {
            $(table).find('tr').each((__: any, tr: any) => {
              const cells: string[] = [];
              $(tr).find('td').each((___: any, td: any) => cells.push($(td).text().trim()));
              if (cells.length === tableHeaders.length) {
                const record: Record<string, unknown> = { _source_url: url };
                tableHeaders.forEach((h, i) => { record[h] = cells[i]; });
                records.push(record);
                hasTableData = true;
              }
            });
          }
        });

        // If no tables, extract page as a content record
        if (!hasTableData) {
          records.push({
            _source_url: url,
            title,
            headings: headings.join('; '),
            content: paragraphs.slice(0, 10).join('\n\n'),
          });
        }

        // Discover links for crawling (same domain only)
        const baseUrl = new URL(url);
        $('a[href]').each((_: any, el: any) => {
          try {
            const href = $(el).attr('href')!;
            const linkUrl = new URL(href, url);
            if (linkUrl.hostname === baseUrl.hostname && !visited.has(linkUrl.href)) {
              queue.push(linkUrl.href);
            }
          } catch {
            // Skip invalid URLs
          }
        });
      } catch (err) {
        const msg = err instanceof Error ? err.message : String(err);
        process.stderr.write(`  Warning: Failed to crawl ${url}: ${msg}\n`);
      }
    }

    if (records.length === 0) {
      throw new Error('No content extracted from web pages');
    }

    return {
      records,
      format: 'json',
      sourceName: new URL(this.options.url).hostname,
    };
  }
}
