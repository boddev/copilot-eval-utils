import * as fs from 'fs';
import * as path from 'path';
import { TextDecoder } from 'util';
import { parse } from 'csv-parse/sync';
import * as XLSX from 'xlsx';
import { XMLParser } from 'fast-xml-parser';
import { InputFormat } from '../types';

export interface ReadResult {
  records: Record<string, unknown>[];
  format: InputFormat;
}

export interface ReadDatasetOptions {
  /** Directory traversal is recursive by default so dataset folders can contain source subdirectories. */
  recursive?: boolean;
  /** Optional extension allow-list, without dots (for example: ["csv", "json"]). */
  extensions?: string[];
}

/** Supported file extensions for auto-discovery in directories */
const SUPPORTED_EXTENSIONS = new Set([
  '.csv', '.tsv', '.json', '.jsonl', '.xlsx', '.xls',
  '.docx', '.pdf', '.pptx', '.txt', '.md',
]);

/** Target size for content chunks emitted by document readers (DOCX/PDF/PPTX/TXT). */
const CHUNK_TARGET_CHARS = 500;

/** Permissive boolean env-var parsing: true/1/yes/on (case-insensitive). */
function parseBoolEnv(value: string | undefined): boolean {
  if (!value) return false;
  return /^(true|1|yes|on)$/i.test(value.trim());
}

/** Detect file format from extension */
function detectFormat(filePath: string): InputFormat {
  const ext = path.extname(filePath).toLowerCase();
  switch (ext) {
    case '.csv': return 'csv';
    case '.tsv': return 'csv';
    case '.json': return 'json';
    case '.jsonl': return 'jsonl';
    case '.xlsx':
    case '.xls': return 'xlsx';
    case '.docx': return 'docx';
    case '.pdf': return 'pdf';
    case '.pptx': return 'pptx';
    case '.txt':
    case '.md': return 'txt';
    default:
      throw new Error(`Unsupported file format: ${ext}. Supported: csv, json, xlsx, docx, pdf, pptx, txt`);
  }
}

/** Read CSV/TSV file */
function readCsv(filePath: string): Record<string, unknown>[] {
  const content = fs.readFileSync(filePath, 'utf-8');
  const delimiter = filePath.endsWith('.tsv') ? '\t' : ',';
  return parse(content, {
    columns: true,
    skip_empty_lines: true,
    trim: true,
    delimiter,
  }) as Record<string, unknown>[];
}

/** Read JSON file (array of objects) */
function readJson(filePath: string): Record<string, unknown>[] {
  const content = fs.readFileSync(filePath, 'utf-8');
  const parsed = JSON.parse(content);
  if (Array.isArray(parsed)) return parsed;
  if (parsed && typeof parsed === 'object') return [parsed];
  throw new Error('JSON file must contain an array of objects or a single object');
}

/** Read JSONL file (one JSON object per line) */
function readJsonl(filePath: string): Record<string, unknown>[] {
  const records: Record<string, unknown>[] = [];
  const fd = fs.openSync(filePath, 'r');
  const buffer = Buffer.allocUnsafe(1024 * 1024);
  const decoder = new TextDecoder('utf-8');
  let remainder = '';
  let lineNumber = 1;

  const parseLine = (line: string, currentLineNumber: number): void => {
    const trimmed = (currentLineNumber === 1 ? line.replace(/^\uFEFF/, '') : line).trim();
    if (trimmed.length === 0) return;
    try {
      records.push(JSON.parse(trimmed) as Record<string, unknown>);
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      throw new Error(`Invalid JSONL in ${filePath} at line ${currentLineNumber}: ${message}`);
    }
  };

  try {
    let bytesRead = 0;
    do {
      bytesRead = fs.readSync(fd, buffer, 0, buffer.length, null);
      const decoded = bytesRead > 0
        ? decoder.decode(buffer.subarray(0, bytesRead), { stream: true })
        : decoder.decode();
      if (decoded.length === 0 && bytesRead > 0) continue;

      const lines = (remainder + decoded).split('\n');
      remainder = lines.pop() ?? '';
      for (const line of lines) {
        parseLine(line, lineNumber);
        lineNumber++;
      }
    } while (bytesRead > 0);

    if (remainder.trim().length > 0) {
      parseLine(remainder, lineNumber);
    }
  } finally {
    fs.closeSync(fd);
  }

  return records;
}

/** Read XLSX file (first sheet as tabular data) */
function readXlsx(filePath: string): Record<string, unknown>[] {
  const workbook = XLSX.readFile(filePath);
  const sheetName = workbook.SheetNames[0];
  if (!sheetName) throw new Error('XLSX file has no sheets');
  const sheet = workbook.Sheets[sheetName];
  return XLSX.utils.sheet_to_json(sheet) as Record<string, unknown>[];
}

/**
 * Chunk a long text into ~CHUNK_TARGET_CHARS records, splitting on paragraph
 * boundaries to keep boundaries stable across runs.
 */
function chunkText(paragraphs: string[]): Record<string, unknown>[] {
  const records: Record<string, unknown>[] = [];
  let chunk = '';
  let chunkNum = 1;
  const flush = (): void => {
    const trimmed = chunk.trim();
    if (trimmed.length === 0) return;
    records.push({
      chunk_number: chunkNum++,
      content: trimmed,
      word_count: trimmed.split(/\s+/).filter(w => w.length > 0).length,
    });
    chunk = '';
  };
  for (const para of paragraphs) {
    const piece = para.trim();
    if (piece.length === 0) continue;
    if (chunk.length + piece.length > CHUNK_TARGET_CHARS && chunk.length > 0) {
      flush();
    }
    chunk += (chunk.length === 0 ? '' : '\n') + piece;
  }
  flush();
  return records;
}

/**
 * Read PPTX file — extracts text from each slide, including title placeholder,
 * body paragraphs, and any associated speaker notes.
 *
 * Implementation notes:
 *   - Uses adm-zip + fast-xml-parser. Entries are first enumerated *by name*
 *     and `getData()` is only called for the small handful of XML parts we
 *     actually parse (slides, notes, presentation rels, slide rels, and —
 *     optionally — masters/layouts). This avoids eagerly decompressing
 *     embedded images, video, or OLE objects in large real-world decks.
 *   - Slide order and per-slide notes mapping come from OPC relationships
 *     (`ppt/_rels/presentation.xml.rels` + `ppt/slides/_rels/slideN.xml.rels`),
 *     not from filename digits. This is what the PPTX spec actually
 *     guarantees and survives slide reorder/delete.
 *   - The XML parser is configured with `removeNSPrefix: true` so the
 *     walker matches local names (`p`, `t`, `sld`, etc.) regardless of
 *     which prefix the source file happens to use.
 *   - Slide master / layout text (e.g. "Confidential" footers) is only
 *     surfaced when the caller opts in via the env var
 *     `EVALGEN_PPTX_INCLUDE_MASTER=true`. Default off because that text
 *     would otherwise dominate every record sample (profiler always picks
 *     the last record, and boilerplate repeats across every deck).
 */
async function readPptx(filePath: string): Promise<Record<string, unknown>[]> {
  const AdmZip = requireOptional('adm-zip',
    'PPTX support requires the "adm-zip" package. Install: npm install adm-zip');
  const zip = new AdmZip(filePath);

  // 1. Build a lazy lookup of entryName -> entry. No getData() yet.
  const entriesByName = new Map<string, { getData: () => Buffer }>();
  for (const entry of zip.getEntries()) {
    entriesByName.set(entry.entryName as string, entry);
  }

  const readEntry = (name: string): Buffer | undefined => {
    const e = entriesByName.get(name);
    return e ? e.getData() : undefined;
  };

  // 2. Resolve slide order via presentation.xml + its rels. Fall back to
  //    numeric file-name sort if either is missing (older / malformed decks).
  const slideTargets = resolveSlideOrder(readEntry, entriesByName.keys());

  if (slideTargets.length === 0) {
    throw new Error('No slides found in PPTX file');
  }

  const records: Record<string, unknown>[] = [];

  for (let i = 0; i < slideTargets.length; i++) {
    const slideTarget = slideTargets[i];
    const slidePath = slideTarget.path;
    const buf = readEntry(slidePath);
    if (!buf) continue;
    const paragraphs = extractDrawingMlParagraphs(buf);
    if (paragraphs.length === 0) continue;
    const title = paragraphs[0];
    const body = paragraphs.slice(1);

    // Resolve speaker notes via the slide's own rels file. The notes part
    // is referenced by Type=".../notesSlide" Target="../notesSlides/...".
    const notesPath = resolveSlideNotesTarget(readEntry, slidePath);
    const notesBuf = notesPath ? readEntry(notesPath) : undefined;
    const notes = notesBuf
      ? extractDrawingMlParagraphs(notesBuf).join('\n').trim()
      : '';

    records.push({
      slide_number: i + 1,
      title,
      content: body.join('\n'),
      notes,
    });
  }

  // Optional: surface slide master + layout text (e.g. confidentiality
  // markings). Off by default — see file header for rationale.
  if (parseBoolEnv(process.env.EVALGEN_PPTX_INCLUDE_MASTER)) {
    const masterParas: string[] = [];
    for (const name of entriesByName.keys()) {
      if (/^ppt\/slideMasters\/slideMaster\d+\.xml$/.test(name) ||
          /^ppt\/slideLayouts\/slideLayout\d+\.xml$/.test(name)) {
        const buf = readEntry(name);
        if (buf) masterParas.push(...extractDrawingMlParagraphs(buf));
      }
    }
    const masterText = masterParas.join('\n').trim();
    if (masterText.length > 0) {
      records.push({
        slide_number: 0,
        title: '(slide master / layout)',
        content: masterText,
        notes: '',
      });
    }
  }

  if (records.length === 0) {
    throw new Error('No text content found in PPTX file');
  }
  return records;
}

interface SlideTarget { path: string }

/**
 * Resolve the ordered list of slide part paths via OPC relationships.
 * Returns absolute (package-relative) paths like `ppt/slides/slide1.xml`.
 *
 * Falls back to lexicographic-by-numeric-suffix scan over the supplied
 * `entryNames` if the spec parts are missing or unparseable (older or
 * minimal decks may not have parseable presentation rels).
 */
function resolveSlideOrder(
  readEntry: (name: string) => Buffer | undefined,
  entryNames: Iterable<string>,
): SlideTarget[] {
  const presentationBuf = readEntry('ppt/presentation.xml');
  const relsBuf = readEntry('ppt/_rels/presentation.xml.rels');

  if (presentationBuf && relsBuf) {
    const ridOrder = extractSlideRidOrder(presentationBuf);
    const ridToTarget = extractRelationshipMap(relsBuf);
    if (ridOrder.length > 0) {
      const result: SlideTarget[] = [];
      for (const rid of ridOrder) {
        const target = ridToTarget.get(rid);
        if (!target) continue;
        // Relationship targets in presentation.xml.rels are relative to
        // `ppt/`. Normalize to a package-absolute path.
        result.push({ path: resolveRelTarget('ppt', target) });
      }
      if (result.length > 0) return result;
    }
  }

  // Fallback: scan entries for slide parts and sort numerically. This is
  // not spec-correct for decks that reordered or deleted slides without
  // renaming files, but it lets minimal or malformed decks still parse.
  const slidePaths: { path: string; num: number }[] = [];
  for (const name of entryNames) {
    const m = /^ppt\/slides\/slide(\d+)\.xml$/.exec(name);
    if (m) slidePaths.push({ path: name, num: parseInt(m[1], 10) });
  }
  slidePaths.sort((a, b) => a.num - b.num);
  return slidePaths.map(s => ({ path: s.path }));
}

/**
 * Extract rId order from `<p:sldIdLst>` in `presentation.xml`.
 *
 * NOTE: we deliberately do **not** use `removeNSPrefix: true` here because
 * `<p:sldId>` carries BOTH an `id` attribute (slide internal id) and an
 * `r:id` attribute (the relationship reference). Collapsing them would
 * make the result depend on attribute order. Per the OOXML spec the
 * `r:` prefix is always the relationships namespace, so we can safely
 * match exact attribute names.
 */
function extractSlideRidOrder(buffer: Buffer): string[] {
  const parser = new XMLParser({
    ignoreAttributes: false,
    attributeNamePrefix: '@_',
    preserveOrder: true,
  });
  const parsed = parser.parse(buffer.toString('utf-8')) as unknown;
  const rids: string[] = [];
  walkForElementAttrs(parsed, 'p:sldId', (attrs) => {
    const rid = attrs?.['@_r:id'];
    if (typeof rid === 'string' && rid.startsWith('rId')) rids.push(rid);
  });
  return rids;
}

/**
 * Parse an `.rels` Relationships document into Map<rId, Target>.
 *
 * NOTE: `removeNSPrefix` is off — see `extractSlideRidOrder` for the
 * rationale. The Relationships document is in the OPC relationships
 * namespace and uses unprefixed `Id`/`Target` attributes per spec.
 */
function extractRelationshipMap(buffer: Buffer): Map<string, string> {
  const parser = new XMLParser({
    ignoreAttributes: false,
    attributeNamePrefix: '@_',
    preserveOrder: true,
  });
  const parsed = parser.parse(buffer.toString('utf-8')) as unknown;
  const map = new Map<string, string>();
  walkForElementAttrs(parsed, 'Relationship', (attrs) => {
    const id = attrs?.['@_Id'];
    const target = attrs?.['@_Target'];
    if (typeof id === 'string' && typeof target === 'string') {
      map.set(id, target);
    }
  });
  return map;
}

/**
 * Locate `<Relationship Type=".../notesSlide" Target="..."/>` in a slide's
 * `.rels` file, returning the package-absolute notes part path.
 */
function resolveSlideNotesTarget(
  readEntry: (name: string) => Buffer | undefined,
  slidePath: string,
): string | undefined {
  // slidePath = 'ppt/slides/slide1.xml' → 'ppt/slides/_rels/slide1.xml.rels'
  const lastSlash = slidePath.lastIndexOf('/');
  const dir = slidePath.substring(0, lastSlash);
  const file = slidePath.substring(lastSlash + 1);
  const relsPath = `${dir}/_rels/${file}.rels`;
  const relsBuf = readEntry(relsPath);
  if (!relsBuf) return undefined;

  // Same attribute-disambiguation reasoning as extractRelationshipMap.
  const parser = new XMLParser({
    ignoreAttributes: false,
    attributeNamePrefix: '@_',
    preserveOrder: true,
  });
  const parsed = parser.parse(relsBuf.toString('utf-8')) as unknown;
  let notesTarget: string | undefined;
  walkForElementAttrs(parsed, 'Relationship', (attrs) => {
    const type = attrs?.['@_Type'];
    const target = attrs?.['@_Target'];
    if (typeof type === 'string' && typeof target === 'string'
        && type.endsWith('/notesSlide')) {
      notesTarget = target;
    }
  });
  if (!notesTarget) return undefined;
  return resolveRelTarget(dir, notesTarget);
}

/**
 * Resolve an OPC relationship `Target` (typically `../foo/bar.xml` or a
 * sibling-relative path) against the directory of the source part.
 */
function resolveRelTarget(sourceDir: string, target: string): string {
  if (target.startsWith('/')) {
    // Absolute package path.
    return target.replace(/^\//, '');
  }
  const parts = sourceDir.split('/').filter(p => p.length > 0);
  const targetParts = target.split('/');
  for (const seg of targetParts) {
    if (seg === '..') {
      parts.pop();
    } else if (seg !== '.' && seg !== '') {
      parts.push(seg);
    }
  }
  return parts.join('/');
}

/**
 * Walk a `preserveOrder: true` tree and invoke `visit(attrs, children)` for
 * every wrapper object whose payload key is `tagName`. Attributes live on the
 * wrapper's sibling `:@` key (fast-xml-parser convention), not on the
 * children array — so a tag walker that recurses through values alone would
 * miss them. This walker recurses *through* the children too so nested
 * `<Relationship>` etc. are found.
 */
function walkForElementAttrs(
  node: unknown,
  tagName: string,
  visit: (attrs: Record<string, unknown>, children: unknown) => void,
): void {
  if (Array.isArray(node)) {
    for (const item of node) {
      if (item && typeof item === 'object' && !Array.isArray(item)) {
        const obj = item as Record<string, unknown>;
        if (Object.prototype.hasOwnProperty.call(obj, tagName)) {
          const attrs = (obj[':@'] as Record<string, unknown> | undefined) ?? {};
          visit(attrs, obj[tagName]);
        }
        for (const [k, v] of Object.entries(obj)) {
          if (k === ':@') continue;
          walkForElementAttrs(v, tagName, visit);
        }
      } else {
        walkForElementAttrs(item, tagName, visit);
      }
    }
    return;
  }
  if (node && typeof node === 'object') {
    const obj = node as Record<string, unknown>;
    if (Object.prototype.hasOwnProperty.call(obj, tagName)) {
      const attrs = (obj[':@'] as Record<string, unknown> | undefined) ?? {};
      visit(attrs, obj[tagName]);
    }
    for (const [k, v] of Object.entries(obj)) {
      if (k === ':@') continue;
      walkForElementAttrs(v, tagName, visit);
    }
  }
}

/**
 * Walk a DrawingML XML buffer (used by PPTX slides, notes, masters, layouts)
 * and return one string per paragraph (`a:p` / local name `p`), with all
 * text runs (`a:t` / local name `t`) concatenated in document order. Empty
 * paragraphs are skipped.
 *
 * With `removeNSPrefix: true` we match on local names so files using
 * non-standard namespace prefixes still parse.
 */
function extractDrawingMlParagraphs(buffer: Buffer): string[] {
  const parser = new XMLParser({
    ignoreAttributes: true,
    removeNSPrefix: true,
    preserveOrder: true,
    parseTagValue: false,
    trimValues: false,
  });
  const parsed = parser.parse(buffer.toString('utf-8')) as unknown;
  const paragraphs: string[] = [];
  walkForTag(parsed, 'p', (node) => {
    const text = collectText(node, 't');
    if (text.trim().length > 0) paragraphs.push(text);
  });
  return paragraphs;
}

/** Recursively call `visit` on every node whose key is `tagName`. */
function walkForTag(node: unknown, tagName: string, visit: (n: unknown) => void): void {
  if (Array.isArray(node)) {
    for (const child of node) walkForTag(child, tagName, visit);
    return;
  }
  if (node && typeof node === 'object') {
    for (const [key, value] of Object.entries(node as Record<string, unknown>)) {
      if (key === tagName) {
        visit(value);
      } else {
        walkForTag(value, tagName, visit);
      }
    }
  }
}

/** Collect concatenated text values of all `tagName` leaves under `node`. */
function collectText(node: unknown, tagName: string): string {
  const out: string[] = [];
  const collect = (n: unknown): void => {
    if (Array.isArray(n)) {
      for (const child of n) collect(child);
      return;
    }
    if (n && typeof n === 'object') {
      for (const [key, value] of Object.entries(n as Record<string, unknown>)) {
        if (key === tagName) {
          collect(value);
        } else if (key === '#text' && typeof value === 'string') {
          out.push(value);
        } else {
          collect(value);
        }
      }
    } else if (typeof n === 'string') {
      out.push(n);
    }
  };
  collect(node);
  return out.join('');
}

/**
 * Read DOCX file — extracts paragraph text via mammoth (which actually walks
 * the OpenXml document structure: paragraphs, lists, tables, headers), then
 * chunks into ~CHUNK_TARGET_CHARS records.
 */
async function readDocx(filePath: string): Promise<Record<string, unknown>[]> {
  const mammoth = requireOptional('mammoth',
    'DOCX support requires the "mammoth" package. Install: npm install mammoth');

  const buffer = fs.readFileSync(filePath);
  const { value } = await mammoth.extractRawText({ buffer }) as { value: string };
  const text = value ?? '';

  // mammoth separates paragraphs with \n (single newline). Split on \n and
  // drop empties. Stable, deterministic ordering is preserved.
  const paragraphs = text.split(/\r?\n/).map(p => p.trim()).filter(p => p.length > 0);

  const records = chunkText(paragraphs);
  if (records.length === 0) {
    throw new Error('No text content found in DOCX file');
  }
  return records;
}

/**
 * Read PDF file — extracts text via pdf-parse v2's PDFParse class (handles
 * FlateDecode-compressed streams, multi-page documents, and standard
 * encodings via pdf.js under the hood) and chunks into ~CHUNK_TARGET_CHARS
 * records.
 */
async function readPdf(filePath: string): Promise<Record<string, unknown>[]> {
  const pdfParseModule = requireOptional('pdf-parse',
    'PDF support requires the "pdf-parse" package. Install: npm install pdf-parse');
  const PDFParse = pdfParseModule.PDFParse ?? pdfParseModule.default?.PDFParse ?? pdfParseModule;
  if (typeof PDFParse !== 'function') {
    throw new Error('pdf-parse: unable to locate PDFParse class on the imported module');
  }

  const buffer = fs.readFileSync(filePath);
  const parser = new PDFParse({ data: buffer });
  let result: { text: string };
  try {
    result = await parser.getText() as { text: string };
  } finally {
    if (typeof parser.destroy === 'function') {
      await parser.destroy();
    }
  }
  // pdf-parse joins page text with \n. Strip control characters and collapse
  // runs of whitespace within a line, but preserve line breaks.
  const cleaned = (result.text ?? '')
    // eslint-disable-next-line no-control-regex
    .replace(/[\x00-\x08\x0B\x0C\x0E-\x1F]/g, '')
    .replace(/\r\n?/g, '\n');

  const paragraphs = cleaned
    .split(/\n{1,}/)
    .map(p => p.replace(/[ \t]+/g, ' ').trim())
    .filter(p => p.length > 0);

  const records = chunkText(paragraphs);
  if (records.length === 0) {
    throw new Error('No text content found in PDF file');
  }
  return records;
}

/** Read plain text / markdown file — chunks into records */
function readTextFile(filePath: string): Record<string, unknown>[] {
  const content = fs.readFileSync(filePath, 'utf-8');
  if (content.trim().length === 0) {
    throw new Error(`Text file is empty: ${filePath}`);
  }

  // Split by double newlines (paragraphs) or headings
  const sections = content.split(/\n{2,}|(?=^#{1,3}\s)/m).filter(s => s.trim().length > 0);

  // Chunk into ~500 char records
  const records: Record<string, unknown>[] = [];
  let chunk = '';
  let chunkNum = 1;

  for (const section of sections) {
    if (chunk.length + section.length > 500 && chunk.length > 0) {
      records.push({
        chunk_number: chunkNum++,
        content: chunk.trim(),
        word_count: chunk.trim().split(/\s+/).length,
      });
      chunk = '';
    }
    chunk += section + '\n\n';
  }

  if (chunk.trim().length > 0) {
    records.push({
      chunk_number: chunkNum,
      content: chunk.trim(),
      word_count: chunk.trim().split(/\s+/).length,
    });
  }

  return records;
}

/** Helper to require an optional dependency with a clear error message */
function requireOptional(packageName: string, errorMessage: string): any {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    return require(packageName);
  } catch {
    throw new Error(errorMessage);
  }
}

/** Read a single dataset file and return records */
async function readSingleFile(absPath: string): Promise<ReadResult> {
  const format = detectFormat(absPath);

  let records: Record<string, unknown>[];
  switch (format) {
    case 'csv':
      records = readCsv(absPath);
      break;
    case 'json':
      records = readJson(absPath);
      break;
    case 'jsonl':
      records = readJsonl(absPath);
      break;
    case 'xlsx':
      records = readXlsx(absPath);
      break;
    case 'docx':
      records = await readDocx(absPath);
      break;
    case 'pdf':
      records = await readPdf(absPath);
      break;
    case 'pptx':
      records = await readPptx(absPath);
      break;
    case 'txt':
      records = readTextFile(absPath);
      break;
    default:
      throw new Error(`Unsupported format: ${format}`);
  }

  return { records, format };
}

/**
 * Discover all supported files in a directory (non-recursive)
 */
function discoverFilesInDirectory(dirPath: string, options: ReadDatasetOptions): string[] {
  const entries = fs.readdirSync(dirPath, { withFileTypes: true });
  const extensionFilter = options.extensions
    ? new Set(options.extensions.map(e => e.replace(/^\./, '').toLowerCase()))
    : undefined;
  const files: string[] = [];

  for (const entry of entries) {
    const entryPath = path.join(dirPath, entry.name);
    if (entry.isDirectory() && options.recursive !== false) {
      files.push(...discoverFilesInDirectory(entryPath, options));
      continue;
    }

    if (!entry.isFile()) continue;

    const extensionWithDot = path.extname(entry.name).toLowerCase();
    const extension = extensionWithDot.replace(/^\./, '');
    if (!SUPPORTED_EXTENSIONS.has(extensionWithDot)) continue;
    if (extensionFilter && !extensionFilter.has(extension)) continue;

    files.push(entryPath);
  }

  return files.sort();
}

/**
 * Read a dataset from one or more file paths, a directory, or a comma-separated list.
 *
 * Supports:
 * - Single file: "data.csv"
 * - Directory: "data/" → reads all CSV/JSON/XLSX files inside
 * - Comma-separated: "part1.csv,part2.csv,part3.csv"
 *
 * All records are tagged with `_source_file` for provenance.
 */
export async function readDatasetFile(fileInput: string, options: ReadDatasetOptions = {}): Promise<ReadResult & { sourceFiles: string[] }> {
  // Split on commas to support multiple files
  const inputs = fileInput.split(',').map(s => s.trim()).filter(s => s.length > 0);
  const filesToRead: string[] = [];

  for (const input of inputs) {
    const absPath = path.resolve(input);
    if (!fs.existsSync(absPath)) {
      throw new Error(`File or directory not found: ${absPath}`);
    }

    const stat = fs.statSync(absPath);
    if (stat.isDirectory()) {
      const discovered = discoverFilesInDirectory(absPath, options);
      if (discovered.length === 0) {
        throw new Error(`No supported files found in directory: ${absPath}`);
      }
      filesToRead.push(...discovered);
    } else {
      filesToRead.push(absPath);
    }
  }

  if (filesToRead.length === 0) {
    throw new Error('No files to read');
  }

  // Read all files, tag records with source file
  const allRecords: Record<string, unknown>[] = [];
  let primaryFormat: InputFormat = 'csv';

  for (const filePath of filesToRead) {
    const { records, format } = await readSingleFile(filePath);
    primaryFormat = format; // Last format wins (for profiling)

    // Tag each record with its source file
    const fileName = path.basename(filePath);
    for (const record of records) {
      record._source_file = fileName;
      allRecords.push(record);
    }
  }

  if (allRecords.length === 0) {
    throw new Error('All dataset files are empty');
  }

  if (filesToRead.length > 1) {
    process.stderr.write(`  Merged ${filesToRead.length} files: ${filesToRead.map(f => path.basename(f)).join(', ')}\n`);
  }

  return {
    records: allRecords,
    format: primaryFormat,
    sourceFiles: filesToRead.map(f => path.basename(f)),
  };
}
