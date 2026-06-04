# Changelog

All notable changes to `eval-gen` are documented here.

## Unreleased

### Fixed — PDF / DOCX / PPTX readers now produce real document text

Previously the readers for these three formats were stubbed or used naive
regex extraction that bypassed the libraries they imported:

- **PDF** (`readPdf`) imported `pdf-parse` but never called it. For any
  FlateDecode-compressed PDF (essentially every real-world PDF) it
  returned a literal placeholder string of the form
  `[PDF file: <name>. Text extraction requires async processing. Use
  pdf-parse for full extraction.]`. Eval sets generated from PDF sources
  were therefore built from the placeholder, not from the document.
- **DOCX** (`readDocx`) imported `mammoth` but never called it. Extracted
  text via regex over `<w:t>` runs in `word/document.xml` only, losing
  paragraph structure, ordering, tables, lists, headers/footers, and
  drawing-anchored text.
- **PPTX** (`readPptx`) used regex over `<a:t>`. Lost speaker notes,
  slide layouts, master text, and ordering across grouped/embedded shapes.

These have been replaced with proper implementations:

- `readPdf` now calls `pdf-parse` v2 (`new PDFParse({ data }).getText()`)
  and chunks the extracted text into ~500-character records on paragraph
  boundaries.
- `readDocx` now calls `mammoth.extractRawText({ buffer })` so paragraph
  ordering and structure-walked text are preserved before chunking.
- `readPptx` now uses `adm-zip` + `fast-xml-parser` to walk the Open XML
  package properly. Slide order and speaker-notes mapping come from OPC
  relationships (`ppt/_rels/presentation.xml.rels` and each slide's
  `_rels/slideN.xml.rels`), not from filename digits, so reordered or
  partially-deleted decks emit records in their true presentation order.

#### Behavioral notes for upgrading

- Eval sets previously generated from PDF / DOCX / PPTX sources reference
  the old stub/regex output. Existing sidecar JSON files and CSVs remain
  valid for re-use, but if you want eval rows that reflect the actual
  document content you should **regenerate the eval set** from the same
  source files using the updated reader.
- The readers became `async` (they were called from already-async
  pipeline code, so there is no public API impact).
- PPTX records now include an optional `notes` field carrying the
  slide's speaker notes. Notes are no longer also inlined into the
  `content` field; the profiler enumerates record fields dynamically so
  notes still reach the LLM via the new column.
- PPTX slide-master / layout text (e.g. "Confidential" footers) is
  surfaced only when `EVALGEN_PPTX_INCLUDE_MASTER=true` is set in the
  environment. The default is to omit it so per-slide boilerplate does
  not dominate sampled rows.

### Added

- New reader test suite in `tests/readers.test.ts` covers real
  PDF / DOCX / PPTX fixtures generated on the fly (no committed binary
  blobs) via `tests/fixtures/build-doc-fixtures.ts`.
- New runtime dependency: `fast-xml-parser` (for the PPTX walker).
- New development dependencies: `pdfkit` and `@types/pdfkit` (used only
  by the fixture builder to produce real compressed PDFs at test time).
- Reader tests now run as a blocking step in the
  `Build Eval UI Windows release` workflow.
