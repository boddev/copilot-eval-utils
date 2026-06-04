import { describe, it, expect, beforeAll } from 'vitest';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { extractPptxSlideText, readDatasetFile } from '../src/readers';
import { buildSamplePdf, buildSampleDocx, buildSamplePptx } from './fixtures/build-doc-fixtures';

const FIXTURES = path.join(__dirname, 'fixtures');

describe('readDatasetFile', () => {
  it('reads CSV files correctly', async () => {
    const result = await readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    expect(result.format).toBe('csv');
    expect(result.records.length).toBe(15);
    expect(result.records[0]).toHaveProperty('supplier_name', 'Acme Corp');
  });

  it('reads JSON files correctly', async () => {
    const result = await readDatasetFile(path.join(FIXTURES, 'projects.json'));
    expect(result.format).toBe('json');
    expect(result.records.length).toBe(5);
    expect(result.records[0]).toHaveProperty('project_name', 'Apollo');
  });

  it('reads JSONL files without loading the whole file as one string', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'eval-gen-jsonl-'));
    const filePath = path.join(tempDir, 'records.jsonl');
    const longValue = 'x'.repeat(1024 * 1024 + 10);

    try {
      fs.writeFileSync(
        filePath,
        `${JSON.stringify({ id: 1, name: 'Alpha', notes: longValue })}\r\n\n${JSON.stringify({ id: 2, name: 'Beta' })}\n`,
        'utf-8',
      );

      const result = await readDatasetFile(filePath);

      expect(result.format).toBe('jsonl');
      expect(result.records).toHaveLength(2);
      expect(result.records[0]).toHaveProperty('name', 'Alpha');
      expect(result.records[0]).toHaveProperty('notes', longValue);
      expect(result.records[1]).toHaveProperty('name', 'Beta');
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('throws on missing file', async () => {
    await expect(readDatasetFile('nonexistent.csv')).rejects.toThrow('not found');
  });

  it('throws on unsupported format', async () => {
    await expect(readDatasetFile(path.join(FIXTURES, '..', '..', 'vitest.config.ts'))).rejects.toThrow('Unsupported file format');
  });

  it('reads a directory of files', async () => {
    const result = await readDatasetFile(FIXTURES);
    // Should merge suppliers.csv (15) + projects.json (5) + connector-schema.json (1, but it has contentFields not records)
    expect(result.records.length).toBeGreaterThan(15);
    expect(result.sourceFiles.length).toBeGreaterThanOrEqual(2);
  });

  it('reads comma-separated file list', async () => {
    const csv = path.join(FIXTURES, 'suppliers.csv');
    const json = path.join(FIXTURES, 'projects.json');
    const result = await readDatasetFile(`${csv},${json}`);
    expect(result.records.length).toBe(20); // 15 + 5
    expect(result.sourceFiles.length).toBe(2);
  });

  it('tags records with _source_file', async () => {
    const csv = path.join(FIXTURES, 'suppliers.csv');
    const json = path.join(FIXTURES, 'projects.json');
    const result = await readDatasetFile(`${csv},${json}`);

    const csvRecords = result.records.filter(r => r._source_file === 'suppliers.csv');
    const jsonRecords = result.records.filter(r => r._source_file === 'projects.json');
    expect(csvRecords.length).toBe(15);
    expect(jsonRecords.length).toBe(5);
  });

  it('reads text/markdown files as chunked content', async () => {
    const result = await readDatasetFile(path.join(FIXTURES, 'sample-doc.txt'));
    expect(result.format).toBe('txt');
    expect(result.records.length).toBeGreaterThan(0);
    expect(result.records[0]).toHaveProperty('content');
    expect(result.records[0]).toHaveProperty('chunk_number');

    // Content should contain some of the text
    const allContent = result.records.map(r => r.content).join(' ');
    expect(allContent).toContain('supplier management');
  });

  it('detects document formats from extensions', async () => {
    // These test that format detection errors are distinct from not-found errors
    await expect(readDatasetFile('test.docx')).rejects.toThrow(/not found/i);
    await expect(readDatasetFile('test.pdf')).rejects.toThrow(/not found/i);
    await expect(readDatasetFile('test.pptx')).rejects.toThrow(/not found/i);
  });
});

/**
 * Document reader fixes (Phase 0 of the WinUI 3 companion plan).
 *
 * The previous implementations were stubs: PDF returned a placeholder
 * string for any compressed PDF, DOCX did a flat regex over <w:t> runs
 * without using mammoth, and PPTX missed speaker notes and master text.
 * These tests gate the fixes against a real compressed PDF, a structured
 * DOCX (heading + paragraphs), and a multi-slide PPTX with notes + master.
 */
describe('readDatasetFile — document formats', () => {
  let DOC_DIR: string;
  let PDF: string;
  let DOCX: string;
  let PPTX: string;

  beforeAll(async () => {
    // Generate fixtures into a temp dir (not under FIXTURES/) so they don't
    // pollute the directory-read test above with slow document parsing.
    DOC_DIR = fs.mkdtempSync(path.join(os.tmpdir(), 'eval-gen-docs-'));
    PDF = path.join(DOC_DIR, 'sample.pdf');
    DOCX = path.join(DOC_DIR, 'sample.docx');
    PPTX = path.join(DOC_DIR, 'sample.pptx');
    await buildSamplePdf(PDF);
    buildSampleDocx(DOCX);
    buildSamplePptx(PPTX);
  });

  describe('PDF', () => {
    it('extracts real text from a compressed PDF (not a placeholder)', async () => {
      const result = await readDatasetFile(PDF);
      expect(result.format).toBe('pdf');
      expect(result.records.length).toBeGreaterThan(0);
      const text = result.records.map(r => r.content).join('\n');
      // The placeholder stub looked like "[PDF file: ... requires async processing]";
      // assert we are clearly past that and have the document's real content.
      expect(text).not.toMatch(/\[PDF file:.*requires async processing/);
      expect(text).toContain('Acme');
      expect(text).toContain('14,250');
      expect(text).toContain('Gamma Components');
    });

    it('emits stable chunk shape across runs over the same file', async () => {
      const a = await readDatasetFile(PDF);
      const b = await readDatasetFile(PDF);
      expect(a.records.length).toBe(b.records.length);
      for (let i = 0; i < a.records.length; i++) {
        expect(a.records[i].chunk_number).toBe(b.records[i].chunk_number);
        expect(a.records[i].content).toBe(b.records[i].content);
      }
      // Every record should expose chunk metadata for downstream chunking-aware steps.
      for (const r of a.records) {
        expect(r).toHaveProperty('chunk_number');
        expect(r).toHaveProperty('content');
        expect(r).toHaveProperty('word_count');
      }
    });
  });

  describe('DOCX', () => {
    it('extracts paragraph text (not regex over <w:t>)', async () => {
      const result = await readDatasetFile(DOCX);
      expect(result.format).toBe('docx');
      expect(result.records.length).toBeGreaterThan(0);
      const text = result.records.map(r => r.content).join('\n');
      // Heading + body paragraphs should all appear.
      expect(text).toContain('Quarterly Supplier Review');
      expect(text).toContain('Beta Industries');
      expect(text).toContain('Gamma Components');
      // Heading 2 sections must be present in order.
      const headingIndex = text.indexOf('Beta Industries Underperformance');
      const bodyIndex = text.indexOf('73 percent');
      expect(headingIndex).toBeGreaterThan(-1);
      expect(bodyIndex).toBeGreaterThan(headingIndex);
    });

    it('produces chunks with chunk_number / content / word_count', async () => {
      const result = await readDatasetFile(DOCX);
      for (const r of result.records) {
        expect(r).toHaveProperty('chunk_number');
        expect(r).toHaveProperty('content');
        expect(r).toHaveProperty('word_count');
        expect(typeof r.word_count).toBe('number');
      }
    });

    it('emits stable chunks across runs over the same file', async () => {
      const a = await readDatasetFile(DOCX);
      const b = await readDatasetFile(DOCX);
      expect(a.records.length).toBe(b.records.length);
      for (let i = 0; i < a.records.length; i++) {
        expect(a.records[i].content).toBe(b.records[i].content);
      }
    });
  });

  describe('PPTX', () => {
    it('selects title placeholder text even when the title shape is not first', () => {
      for (const placeholderType of ['title', 'ctrTitle']) {
        const slideXml = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
  <p:cSld>
    <p:spTree>
      <p:sp>
        <p:txBody><a:p><a:r><a:t>Body bullet appears first in XML</a:t></a:r></a:p></p:txBody>
      </p:sp>
      <p:sp>
        <p:nvSpPr><p:nvPr><p:ph type="${placeholderType}"/></p:nvPr></p:nvSpPr>
        <p:txBody><a:p><a:r><a:t>Canonical Slide Title</a:t></a:r></a:p></p:txBody>
      </p:sp>
      <p:sp>
        <p:txBody><a:p><a:r><a:t>Trailing body paragraph</a:t></a:r></a:p></p:txBody>
      </p:sp>
    </p:spTree>
  </p:cSld>
</p:sld>`;

        const result = extractPptxSlideText(slideXml);

        expect(result.title).toBe('Canonical Slide Title');
        expect(result.body).toEqual(['Body bullet appears first in XML', 'Trailing body paragraph']);
      }
    });

    it('falls back to the first paragraph when no title placeholder exists', () => {
      const slideXml = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
  <p:cSld>
    <p:spTree>
      <p:sp>
        <p:txBody><a:p><a:r><a:t>First paragraph fallback title</a:t></a:r></a:p></p:txBody>
      </p:sp>
      <p:sp>
        <p:txBody><a:p><a:r><a:t>Second paragraph body</a:t></a:r></a:p></p:txBody>
      </p:sp>
    </p:spTree>
  </p:cSld>
</p:sld>`;

      const result = extractPptxSlideText(slideXml);

      expect(result.title).toBe('First paragraph fallback title');
      expect(result.body).toEqual(['Second paragraph body']);
    });

    it('numbers hidden slides by presentation order', async () => {
      const result = await readDatasetFile(PPTX);
      const slides = result.records.filter(r => typeof r.slide_number === 'number' && r.slide_number > 0);

      // The fixture marks slide 2 with p:sld show="0". It must still count
      // as slide_number 2 so downstream readers mirror PowerPoint UI numbering.
      expect(slides.map(s => s.slide_number)).toEqual([1, 2, 3]);
      expect(slides[1].title).toBe('Risks and Mitigations');
      expect(slides[2].title).toBe('Next Steps');
    });

    it('extracts slide titles, bodies, and speaker notes', async () => {
      const result = await readDatasetFile(PPTX);
      expect(result.format).toBe('pptx');
      expect(result.records.length).toBeGreaterThanOrEqual(3);

      const slides = result.records.filter(r => typeof r.slide_number === 'number' && r.slide_number > 0);
      expect(slides.length).toBe(3);

      const slide1 = slides.find(r => r.slide_number === 1)!;
      expect(slide1.title).toBe('Quarterly Supplier Review');
      const slide1Content = String(slide1.content);
      expect(slide1Content).toContain('Acme Corporation');
      expect(slide1Content).toContain('Beta Industries');
      // Notes are surfaced via the dedicated `notes` field (not duplicated
      // into `content`). The profiler enumerates all record fields, so notes
      // still reach the LLM — but without inflating token count or per-slide
      // content boundaries.
      expect(String(slide1.notes)).toContain('Phoenix distribution center');

      const slide2 = slides.find(r => r.slide_number === 2)!;
      expect(slide2.title).toBe('Risks and Mitigations');
      expect(String(slide2.content)).toContain('Gamma Components');
      expect(String(slide2.notes)).toContain('QA audit');
    });

    it('does not duplicate notes between `content` and `notes` fields', async () => {
      const result = await readDatasetFile(PPTX);
      const slide1 = result.records.find(r => r.slide_number === 1)!;
      // Notes live in the `notes` column only; the inline duplication into
      // `content` was removed in response to reviewer feedback so PPTX rows
      // don't double-count notes text in token budgets / sampling.
      expect(String(slide1.content)).not.toContain('Speaker notes:');
      expect(String(slide1.content)).not.toContain('Phoenix distribution center');
    });

    it('omits slide master / layout text by default', async () => {
      delete process.env.EVALGEN_PPTX_INCLUDE_MASTER;
      const result = await readDatasetFile(PPTX);
      const master = result.records.find(r => r.slide_number === 0);
      expect(master, 'master record should NOT be emitted by default').toBeUndefined();
    });

    it('surfaces slide master / layout text when EVALGEN_PPTX_INCLUDE_MASTER=true', async () => {
      const prev = process.env.EVALGEN_PPTX_INCLUDE_MASTER;
      process.env.EVALGEN_PPTX_INCLUDE_MASTER = 'true';
      try {
        const result = await readDatasetFile(PPTX);
        const master = result.records.find(r => r.slide_number === 0);
        expect(master, 'expected synthetic master/layout record when opted in').toBeDefined();
        expect(String(master!.content)).toContain('Confidential');
      } finally {
        if (prev === undefined) delete process.env.EVALGEN_PPTX_INCLUDE_MASTER;
        else process.env.EVALGEN_PPTX_INCLUDE_MASTER = prev;
      }
    });

    it('preserves slide ordering across runs', async () => {
      const a = await readDatasetFile(PPTX);
      const b = await readDatasetFile(PPTX);
      const aSlides = a.records.map(r => r.slide_number);
      const bSlides = b.records.map(r => r.slide_number);
      expect(aSlides).toEqual(bSlides);
    });
  });
});
