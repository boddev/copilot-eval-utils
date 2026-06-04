/**
 * Test-only fixture builders for PDF / DOCX / PPTX.
 *
 * These intentionally avoid committing binary blobs to the repo: each test
 * suite that needs one of these formats can call the corresponding builder
 * from a `beforeAll`/`beforeEach` hook and get a real, well-formed file
 * written into a caller-controlled directory.
 *
 * The generated files are deliberately small and stable: the same call
 * arguments always produce semantically equivalent output (same paragraphs,
 * slides, notes, etc.), so reader tests can assert stable chunk counts and
 * recognizable text.
 */

import * as fs from 'fs';
import * as path from 'path';
// pdfkit ships its own CJS entry; require keeps this file friendly under ts-node + vitest.
// eslint-disable-next-line @typescript-eslint/no-require-imports
const PDFDocument = require('pdfkit');
// eslint-disable-next-line @typescript-eslint/no-require-imports
const AdmZip = require('adm-zip');

/** Build a compressed PDF with multiple paragraphs across multiple pages. */
export async function buildSamplePdf(targetPath: string): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    const doc = new PDFDocument({ compress: true });
    const stream = fs.createWriteStream(targetPath);
    stream.on('finish', () => resolve());
    stream.on('error', reject);
    doc.pipe(stream);

    doc.fontSize(18).text('Quarterly Supplier Review', { underline: true });
    doc.moveDown();
    doc.fontSize(11).text(
      'Acme Corporation continues to be our largest supplier of widgets, ' +
      'delivering 14,250 units in the most recent quarter at an average ' +
      'unit cost of $4.21. Lead time held steady at 6 business days.',
    );
    doc.moveDown();
    doc.text(
      'Beta Industries underperformed against contract terms, delivering ' +
      'only 73% of committed gadget volume; root-cause analysis points to ' +
      'a tooling failure at the Tianjin facility that is expected to be ' +
      'resolved by end of next quarter.',
    );
    doc.addPage();
    doc.fontSize(14).text('Page 2: Risks and Recommendations');
    doc.moveDown();
    doc.fontSize(11).text(
      'We recommend onboarding Gamma Components as a second source for ' +
      'gadgets to reduce single-supplier risk. Initial quality samples ' +
      'meet specification and pricing is within 4% of Beta Industries.',
    );
    doc.end();
  });
}

/** Build a minimal but valid DOCX with multiple paragraphs, a heading, and a list. */
export function buildSampleDocx(targetPath: string): void {
  // OPC package: [Content_Types].xml + _rels/.rels + word/document.xml + word/_rels/document.xml.rels
  const contentTypes = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
</Types>`;

  const rootRels = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
</Relationships>`;

  const docRels = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"></Relationships>`;

  const paragraphs = [
    { style: 'Heading1', text: 'Quarterly Supplier Review' },
    { text: 'Acme Corporation continues to be our largest supplier of widgets, delivering 14,250 units in the most recent quarter at an average unit cost of $4.21.' },
    { text: 'Lead time held steady at 6 business days, well within the contractual ceiling of 10 business days.' },
    { style: 'Heading2', text: 'Beta Industries Underperformance' },
    { text: 'Beta Industries delivered only 73 percent of committed gadget volume in Q3, citing a tooling failure at the Tianjin facility.' },
    { text: 'Mitigation: dual-source onboarding for Gamma Components is in flight and on track for go-live next quarter.' },
    { style: 'Heading2', text: 'Recommendations' },
    { text: 'Approve Gamma Components as a second source for gadgets. Initial quality samples meet specification and pricing is within 4 percent of Beta Industries.' },
  ];

  const body = paragraphs.map(p => {
    const styleXml = p.style ? `<w:pPr><w:pStyle w:val="${p.style}"/></w:pPr>` : '';
    return `<w:p>${styleXml}<w:r><w:t xml:space="preserve">${escapeXml(p.text)}</w:t></w:r></w:p>`;
  }).join('');

  const document = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:body>
    ${body}
    <w:sectPr/>
  </w:body>
</w:document>`;

  fs.mkdirSync(path.dirname(targetPath), { recursive: true });
  const zip = new AdmZip();
  zip.addFile('[Content_Types].xml', Buffer.from(contentTypes, 'utf-8'));
  zip.addFile('_rels/.rels', Buffer.from(rootRels, 'utf-8'));
  zip.addFile('word/document.xml', Buffer.from(document, 'utf-8'));
  zip.addFile('word/_rels/document.xml.rels', Buffer.from(docRels, 'utf-8'));
  zip.writeZip(targetPath);
}

/** Build a minimal but valid PPTX with multiple slides, speaker notes, and master text. */
export function buildSamplePptx(targetPath: string): void {
  const contentTypes = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
  <Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>
  <Override PartName="/ppt/slides/slide2.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>
  <Override PartName="/ppt/slides/slide3.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>
  <Override PartName="/ppt/notesSlides/notesSlide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.notesSlide+xml"/>
  <Override PartName="/ppt/notesSlides/notesSlide2.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.notesSlide+xml"/>
  <Override PartName="/ppt/slideLayouts/slideLayout1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"/>
  <Override PartName="/ppt/slideMasters/slideMaster1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"/>
</Types>`;

  const rootRels = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>
</Relationships>`;

  const presentation = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
  <p:sldIdLst>
    <p:sldId id="256" r:id="rId1"/>
    <p:sldId id="257" r:id="rId2"/>
    <p:sldId id="258" r:id="rId3"/>
  </p:sldIdLst>
</p:presentation>`;

  const presentationRels = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/>
  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide2.xml"/>
  <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide3.xml"/>
</Relationships>`;

  const slide = (title: string, bodyParas: string[], hidden = false) => `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<p:sld${hidden ? ' show="0"' : ''} xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
  <p:cSld>
    <p:spTree>
      <p:sp>
        <p:txBody>
          <a:p><a:r><a:t>${escapeXml(title)}</a:t></a:r></a:p>
        </p:txBody>
      </p:sp>
      <p:sp>
        <p:txBody>
          ${bodyParas.map(p => `<a:p><a:r><a:t>${escapeXml(p)}</a:t></a:r></a:p>`).join('')}
        </p:txBody>
      </p:sp>
    </p:spTree>
  </p:cSld>
</p:sld>`;

  const slideRels = (notesTarget?: string) => `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout" Target="../slideLayouts/slideLayout1.xml"/>
  ${notesTarget ? `<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesSlide" Target="${notesTarget}"/>` : ''}
</Relationships>`;

  const notesSlide = (text: string) => `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<p:notes xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
  <p:cSld>
    <p:spTree>
      <p:sp>
        <p:txBody>
          <a:p><a:r><a:t>${escapeXml(text)}</a:t></a:r></a:p>
        </p:txBody>
      </p:sp>
    </p:spTree>
  </p:cSld>
</p:notes>`;

  const slideMaster = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<p:sldMaster xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
  <p:cSld>
    <p:spTree>
      <p:sp>
        <p:txBody>
          <a:p><a:r><a:t>Confidential - Internal Use Only</a:t></a:r></a:p>
        </p:txBody>
      </p:sp>
    </p:spTree>
  </p:cSld>
</p:sldMaster>`;

  const slideLayout = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<p:sldLayout xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
  <p:cSld><p:spTree/></p:cSld>
</p:sldLayout>`;

  fs.mkdirSync(path.dirname(targetPath), { recursive: true });
  const zip = new AdmZip();
  zip.addFile('[Content_Types].xml', Buffer.from(contentTypes, 'utf-8'));
  zip.addFile('_rels/.rels', Buffer.from(rootRels, 'utf-8'));
  zip.addFile('ppt/presentation.xml', Buffer.from(presentation, 'utf-8'));
  zip.addFile('ppt/_rels/presentation.xml.rels', Buffer.from(presentationRels, 'utf-8'));

  zip.addFile('ppt/slides/slide1.xml', Buffer.from(slide(
    'Quarterly Supplier Review',
    ['Q3 results show Acme Corporation continues to lead in widget delivery.', 'Beta Industries underperformed on gadget commitments.'],
  ), 'utf-8'));
  zip.addFile('ppt/slides/_rels/slide1.xml.rels', Buffer.from(slideRels('../notesSlides/notesSlide1.xml'), 'utf-8'));

  zip.addFile('ppt/slides/slide2.xml', Buffer.from(slide(
    'Risks and Mitigations',
    ['Single-source risk for gadgets remains elevated.', 'Onboarding Gamma Components as second source.'],
    true,
  ), 'utf-8'));
  zip.addFile('ppt/slides/_rels/slide2.xml.rels', Buffer.from(slideRels('../notesSlides/notesSlide2.xml'), 'utf-8'));

  zip.addFile('ppt/slides/slide3.xml', Buffer.from(slide(
    'Next Steps',
    ['Approve dual-source contract by end of month.', 'Schedule monthly QBR with Beta Industries leadership.'],
  ), 'utf-8'));
  zip.addFile('ppt/slides/_rels/slide3.xml.rels', Buffer.from(slideRels(undefined), 'utf-8'));

  zip.addFile('ppt/notesSlides/notesSlide1.xml', Buffer.from(notesSlide(
    'Speaker note: emphasize that Acme growth is driven by the new Phoenix distribution center, not pricing concessions.',
  ), 'utf-8'));
  zip.addFile('ppt/notesSlides/notesSlide2.xml', Buffer.from(notesSlide(
    'Speaker note: the Gamma onboarding is contingent on the second QA audit passing next week.',
  ), 'utf-8'));

  zip.addFile('ppt/slideMasters/slideMaster1.xml', Buffer.from(slideMaster, 'utf-8'));
  zip.addFile('ppt/slideLayouts/slideLayout1.xml', Buffer.from(slideLayout, 'utf-8'));

  zip.writeZip(targetPath);
}

function escapeXml(s: string): string {
  return s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}
