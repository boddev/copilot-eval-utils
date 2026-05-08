/* eslint-disable no-console */
const Busboy = require('busboy');
const childProcess = require('child_process');
const fs = require('fs');
const http = require('http');
const path = require('path');
const { pipeline } = require('stream');
const { URL } = require('url');
const { promisify } = require('util');

const streamPipeline = promisify(pipeline);

const HOST = '127.0.0.1';
const DEFAULT_PORT = Number(process.env.EVAL_UI_PORT || 3858);
const MAX_PORT_ATTEMPTS = 10;
const repoRoot = path.resolve(__dirname, '..');
const toolsRoot = path.resolve(process.env.EVAL_UI_TOOLS_ROOT || repoRoot);
const publicDir = path.join(__dirname, 'public');
const workspaceDir = path.resolve(process.env.EVAL_UI_WORKSPACE_DIR || path.join(__dirname, 'workspace'));
const jobsDir = path.join(workspaceDir, 'jobs');
const runtimeDir = path.resolve(process.env.EVAL_UI_RUNTIME_DIR || path.join(__dirname, '.runtime'));
const npmCommand = process.platform === 'win32' ? 'npm.cmd' : 'npm';
const nodeCommand = process.execPath;
const isElectronRuntime = Boolean(process.versions && process.versions.electron);
const isPackagedApp = process.env.EVAL_UI_PACKAGED === '1';
const activeJobs = new Map();
let serverPort = DEFAULT_PORT;
const setupPromises = new Map();

fs.mkdirSync(jobsDir, { recursive: true });
fs.mkdirSync(runtimeDir, { recursive: true });

function nowIso() {
  return new Date().toISOString();
}

function createId() {
  const stamp = new Date().toISOString().replace(/[-:.TZ]/g, '').slice(0, 14);
  const suffix = Math.random().toString(36).slice(2, 8);
  return `${stamp}-${suffix}`;
}

function isSafeJobId(id) {
  return /^[a-zA-Z0-9_-]+$/.test(id);
}

function jobPath(job, ...segments) {
  const resolved = path.resolve(job.dir, ...segments);
  if (resolved !== job.dir && !resolved.startsWith(job.dir + path.sep)) {
    throw new Error('Resolved path escaped the job folder.');
  }
  return resolved;
}

function sanitizePathSegment(segment) {
  const reserved = /^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/i;
  const cleaned = segment
    .replace(/[<>:"|?*\x00-\x1f]/g, '_')
    .replace(/\.+$/g, '')
    .trim();
  const fallback = cleaned || 'file';
  return reserved.test(fallback) ? `${fallback}_` : fallback;
}

function safeUploadPath(filename) {
  const normalized = String(filename || 'upload.bin').replace(/\\/g, '/');
  const parts = normalized
    .split('/')
    .filter((part) => part && part !== '.' && part !== '..')
    .map(sanitizePathSegment);
  return parts.length > 0 ? parts.join(path.sep) : 'upload.bin';
}

function uniqueDestination(root, relativePath) {
  const parsed = path.parse(relativePath);
  const directory = path.resolve(root, parsed.dir);
  if (directory !== root && !directory.startsWith(root + path.sep)) {
    throw new Error('Upload path escaped the dataset folder.');
  }
  fs.mkdirSync(directory, { recursive: true });

  let candidate = path.join(directory, parsed.base);
  let counter = 2;
  while (fs.existsSync(candidate)) {
    candidate = path.join(directory, `${parsed.name}-${counter}${parsed.ext}`);
    counter += 1;
  }
  return candidate;
}

function publicJob(job) {
  return {
    id: job.id,
    status: job.status,
    phase: job.phase,
    createdAt: job.createdAt,
    updatedAt: job.updatedAt,
    settings: job.settings,
    summary: job.summary,
    paths: {
      outputCsv: job.paths.outputCsv,
      sidecar: job.paths.sidecar,
      review: job.paths.review,
      diagnostics: job.paths.diagnostics,
      scoreOutputDir: job.paths.scoreOutputDir,
      report: job.paths.report,
      scoredCsv: job.paths.scoredCsv,
    },
  };
}

function metadataPath(job) {
  return path.join(job.dir, 'job.json');
}

function eventsPath(job) {
  return path.join(job.dir, 'events.jsonl');
}

function saveJob(job) {
  job.updatedAt = nowIso();
  fs.writeFileSync(metadataPath(job), JSON.stringify(publicJob(job), null, 2), 'utf8');
}

function loadJobs() {
  if (!fs.existsSync(jobsDir)) return;
  for (const id of fs.readdirSync(jobsDir)) {
    if (!isSafeJobId(id)) continue;
    const dir = path.join(jobsDir, id);
    const metadataFile = path.join(dir, 'job.json');
    if (!fs.existsSync(metadataFile)) continue;

    try {
      const metadata = JSON.parse(fs.readFileSync(metadataFile, 'utf8'));
      const eventsFile = path.join(dir, 'events.jsonl');
      const events = fs.existsSync(eventsFile)
        ? fs.readFileSync(eventsFile, 'utf8').split(/\r?\n/).filter(Boolean).map((line) => JSON.parse(line))
        : [];
      activeJobs.set(id, {
        id,
        dir,
        status: metadata.status,
        phase: metadata.phase,
        createdAt: metadata.createdAt,
        updatedAt: metadata.updatedAt,
        settings: metadata.settings || {},
        summary: metadata.summary || {},
        paths: metadata.paths || {},
        subscribers: new Set(),
        events,
        nextEventId: events.reduce((max, event) => Math.max(max, event.id || 0), 0),
        activeChild: null,
      });
    } catch (error) {
      console.warn(`Skipping unreadable job metadata at ${metadataFile}: ${error.message}`);
    }
  }
}

function createJob() {
  const id = createId();
  const dir = path.join(jobsDir, id);
  fs.mkdirSync(dir, { recursive: true });
  const job = {
    id,
    dir,
    status: 'created',
    phase: 'created',
    createdAt: nowIso(),
    updatedAt: nowIso(),
    settings: {},
    summary: {},
    paths: {
      datasetDir: path.join(dir, 'dataset'),
      connectorSchemaDir: path.join(dir, 'connector-schema'),
      outputDir: path.join(dir, 'output'),
      scoreOutputDir: path.join(dir, 'scored'),
    },
    subscribers: new Set(),
    events: [],
    nextEventId: 0,
    activeChild: null,
  };
  fs.mkdirSync(job.paths.datasetDir, { recursive: true });
  fs.mkdirSync(job.paths.connectorSchemaDir, { recursive: true });
  fs.mkdirSync(job.paths.outputDir, { recursive: true });
  fs.mkdirSync(job.paths.scoreOutputDir, { recursive: true });
  activeJobs.set(id, job);
  saveJob(job);
  return job;
}

function writeSse(res, event) {
  res.write(`id: ${event.id}\n`);
  res.write(`event: ${event.kind}\n`);
  res.write(`data: ${JSON.stringify(event)}\n\n`);
}

function emit(job, kind, message, data = {}) {
  const event = {
    id: ++job.nextEventId,
    timestamp: nowIso(),
    kind,
    message,
    data,
  };
  job.events.push(event);
  fs.appendFileSync(eventsPath(job), `${JSON.stringify(event)}\n`, 'utf8');
  for (const subscriber of job.subscribers) {
    writeSse(subscriber, event);
  }
  return event;
}

function setJobStatus(job, status, phase, message, data = {}) {
  job.status = status;
  job.phase = phase;
  saveJob(job);
  emit(job, 'status', message, { status, phase, ...data });
}

function getJob(id) {
  if (!isSafeJobId(id)) return undefined;
  return activeJobs.get(id);
}

function sendJson(res, statusCode, payload) {
  const body = JSON.stringify(payload);
  res.writeHead(statusCode, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(body),
    'Cache-Control': 'no-store',
  });
  res.end(body);
}

function sendError(res, statusCode, message) {
  sendJson(res, statusCode, { error: message });
}

function checkLocalOrigin(req, res) {
  if (!['POST', 'PUT', 'PATCH', 'DELETE'].includes(req.method || '')) return true;
  const origin = req.headers.origin;
  if (!origin) return true;

  try {
    const originUrl = new URL(origin);
    const localHost = originUrl.hostname === HOST || originUrl.hostname === 'localhost';
    const localPort = Number(originUrl.port || (originUrl.protocol === 'https:' ? 443 : 80)) === serverPort;
    if (localHost && localPort) return true;
  } catch {
    // Fall through to denial.
  }

  sendError(res, 403, 'Request was blocked because it did not come from the local Eval UI page.');
  return false;
}

function readJsonBody(req, limitBytes = 25 * 1024 * 1024) {
  return new Promise((resolve, reject) => {
    let size = 0;
    const chunks = [];
    req.on('data', (chunk) => {
      size += chunk.length;
      if (size > limitBytes) {
        reject(new Error('Request body is too large.'));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => {
      try {
        const text = Buffer.concat(chunks).toString('utf8') || '{}';
        resolve(JSON.parse(text));
      } catch {
        reject(new Error('Request body was not valid JSON.'));
      }
    });
    req.on('error', reject);
  });
}

function serveStatic(req, res, pathname) {
  const requested = pathname === '/' ? '/index.html' : pathname;
  const resolved = path.resolve(publicDir, `.${decodeURIComponent(requested)}`);
  if (resolved !== publicDir && !resolved.startsWith(publicDir + path.sep)) {
    sendError(res, 403, 'Invalid file path.');
    return;
  }
  if (!fs.existsSync(resolved) || fs.statSync(resolved).isDirectory()) {
    sendError(res, 404, 'File not found.');
    return;
  }

  const extension = path.extname(resolved).toLowerCase();
  const contentType = {
    '.html': 'text/html; charset=utf-8',
    '.css': 'text/css; charset=utf-8',
    '.js': 'text/javascript; charset=utf-8',
    '.svg': 'image/svg+xml',
  }[extension] || 'application/octet-stream';

  res.writeHead(200, {
    'Content-Type': contentType,
    'Cache-Control': 'no-store',
  });
  fs.createReadStream(resolved).pipe(res);
}

function normalizeList(value) {
  return String(value || '')
    .split(',')
    .map((item) => item.trim().replace(/^\./, '').toLowerCase())
    .filter(Boolean);
}

function toolPaths() {
  return {
    evalGenDir: path.join(toolsRoot, 'eval-gen'),
    evalGenJs: path.join(toolsRoot, 'eval-gen', 'dist', 'index.js'),
    evalScoreDir: path.join(toolsRoot, 'eval-score', 'node'),
    evalScoreJs: path.join(toolsRoot, 'eval-score', 'node', 'dist', 'index.js'),
  };
}

function childEnvironment(runAsNode = false) {
  const env = {
    ...process.env,
    EVALGEN_LLM_TIMEOUT_MS: process.env.EVALGEN_LLM_TIMEOUT_MS || '600000',
    EVALGEN_LLM_MAX_ATTEMPTS: process.env.EVALGEN_LLM_MAX_ATTEMPTS || '5',
    EVALGEN_LLM_BACKOFF_MS: process.env.EVALGEN_LLM_BACKOFF_MS || '5000',
    EVALSCORE_WORKIQ_TIMEOUT_MS: process.env.EVALSCORE_WORKIQ_TIMEOUT_MS || '600000',
    EVALSCORE_WORKIQ_MAX_ATTEMPTS: process.env.EVALSCORE_WORKIQ_MAX_ATTEMPTS || '5',
    EVALSCORE_WORKIQ_BACKOFF_MS: process.env.EVALSCORE_WORKIQ_BACKOFF_MS || '5000',
  };
  if (runAsNode) {
    env.ELECTRON_RUN_AS_NODE = '1';
  } else {
    delete env.ELECTRON_RUN_AS_NODE;
  }
  return env;
}

function runCommand(job, label, command, args, options = {}) {
  return new Promise((resolve, reject) => {
    emit(job, 'log', `\n${label}`);
    emit(job, 'log', `> ${command} ${args.join(' ')}`);
    const child = childProcess.spawn(command, args, {
      cwd: options.cwd || toolsRoot,
      env: childEnvironment(options.runAsNode),
      windowsHide: true,
      shell: false,
    });
    job.activeChild = child;

    if (child.stdout) child.stdout.on('data', (chunk) => emit(job, 'log', chunk.toString()));
    if (child.stderr) child.stderr.on('data', (chunk) => emit(job, 'log', chunk.toString()));
    child.on('error', (error) => {
      job.activeChild = null;
      reject(error);
    });
    child.on('close', (code) => {
      job.activeChild = null;
      const acceptedCodes = options.acceptedCodes || [0];
      if (acceptedCodes.includes(code)) {
        resolve(code);
      } else {
        reject(new Error(`${label} exited with code ${code}.`));
      }
    });
  });
}

async function ensureTool(job, tool) {
  if (setupPromises.has(tool)) {
    emit(job, 'log', `Waiting for ${tool} setup already in progress...`);
    await setupPromises.get(tool);
    return;
  }

  const setupPromise = (async () => {
    const paths = toolPaths();
    if (tool === 'eval-gen') {
      if (fs.existsSync(paths.evalGenJs)) {
        emit(job, 'log', 'EvalGen is already built.');
        return;
      }
      if (isPackagedApp) {
        throw new Error(`The packaged Eval UI is missing EvalGen at ${paths.evalGenJs}. Rebuild the executable artifact.`);
      }
      setJobStatus(job, job.status, 'setup', 'Preparing EvalGen. This can take a few minutes the first time.');
      await runCommand(job, 'Installing EvalGen dependencies...', npmCommand, ['install', '--prefix', paths.evalGenDir]);
      await runCommand(job, 'Building EvalGen...', npmCommand, ['run', 'build', '--prefix', paths.evalGenDir]);
      return;
    }

    if (tool === 'eval-score') {
      if (fs.existsSync(paths.evalScoreJs)) {
        emit(job, 'log', 'EvalScore is already built.');
        return;
      }
      if (isPackagedApp) {
        throw new Error(`The packaged Eval UI is missing EvalScore at ${paths.evalScoreJs}. Rebuild the executable artifact.`);
      }
      setJobStatus(job, job.status, 'setup', 'Preparing EvalScore. This can take a few minutes the first time.');
      await runCommand(job, 'Installing EvalScore dependencies...', npmCommand, ['install', '--prefix', paths.evalScoreDir]);
      await runCommand(job, 'Building EvalScore...', npmCommand, ['run', 'build', '--prefix', paths.evalScoreDir]);
      return;
    }

    throw new Error(`Unknown tool setup request: ${tool}`);
  })();

  setupPromises.set(tool, setupPromise);
  try {
    await setupPromise;
  } finally {
    setupPromises.delete(tool);
  }
}

async function handleGenerateUpload(req, res) {
  const job = createJob();
  const fields = {};
  const writePromises = [];
  let uploadedDatasetFiles = 0;
  let connectorSchemaPath;
  let uploadError;

  try {
    const busboy = Busboy({ headers: req.headers, preservePath: true });
    busboy.on('field', (name, value) => {
      fields[name] = value;
    });
    busboy.on('file', (fieldName, file, info) => {
      const uploadRoot = fieldName === 'connectorSchema' ? job.paths.connectorSchemaDir : job.paths.datasetDir;
      const relativePath = safeUploadPath(info.filename || `${fieldName}.bin`);
      const destination = uniqueDestination(uploadRoot, relativePath);
      if (fieldName === 'dataset') uploadedDatasetFiles += 1;
      if (fieldName === 'connectorSchema') connectorSchemaPath = destination;
      writePromises.push(streamPipeline(file, fs.createWriteStream(destination)));
    });
    busboy.on('error', (error) => {
      uploadError = error;
    });
    busboy.on('finish', async () => {
      try {
        if (uploadError) {
          throw uploadError;
        }
        await Promise.all(writePromises);
        if (uploadedDatasetFiles === 0) {
          setJobStatus(job, 'failed', 'upload', 'No dataset files were uploaded.');
          sendError(res, 400, 'Choose at least one dataset file or folder before generating.');
          return;
        }

        const description = String(fields.description || '').trim();
        if (!description) {
          setJobStatus(job, 'failed', 'upload', 'A dataset description is required.');
          sendError(res, 400, 'Describe what this dataset contains before generating.');
          return;
        }

        job.settings = {
          description,
          count: Math.min(50, Math.max(10, Number(fields.count || 30))),
          extensions: normalizeList(fields.extensions || 'csv,json,jsonl,xlsx,xls,tsv,docx,pdf,pptx,txt,md'),
          provider: String(fields.provider || 'm365-copilot'),
          model: String(fields.model || '').trim(),
          m365Tenant: String(fields.m365Tenant || '').trim(),
          connectorSchemaPath,
          uploadedDatasetFiles,
        };
        saveJob(job);
        emit(job, 'log', `Uploaded ${uploadedDatasetFiles} dataset file(s).`);
        setJobStatus(job, 'queued', 'generate', 'Dataset uploaded. Starting evaluation generation...');
        sendJson(res, 202, { job: publicJob(job) });
        runGenerate(job).catch((error) => failJob(job, error));
      } catch (error) {
        setJobStatus(job, 'failed', 'upload', error.message);
        sendError(res, 500, error.message);
      }
    });
    req.pipe(busboy);
  } catch (error) {
    setJobStatus(job, 'failed', 'upload', error.message);
    sendError(res, 500, error.message);
  }
}

async function runGenerate(job) {
  setJobStatus(job, 'running', 'setup', 'Checking local tools...');
  await ensureTool(job, 'eval-gen');

  const paths = toolPaths();
  const outputCsv = jobPath(job, 'output', 'eval-set.csv');
  job.paths.outputCsv = outputCsv;
  job.paths.sidecar = outputCsv.replace(/\.(csv|xlsx|json)$/i, '.evalgen.json');
  job.paths.review = outputCsv.replace(/\.(csv|xlsx|json)$/i, '-review.md');
  job.paths.diagnostics = outputCsv.replace(/\.(csv|xlsx|json)$/i, '-diagnostics.md');
  saveJob(job);

  const args = [
    paths.evalGenJs,
    '--file', job.paths.datasetDir,
    '--description', job.settings.description,
    '--count', String(job.settings.count),
    '--output', outputCsv,
    '--provider', job.settings.provider || 'm365-copilot',
  ];

  if (job.settings.extensions && job.settings.extensions.length > 0) {
    args.push('--extensions', job.settings.extensions.join(','));
  }
  if (job.settings.connectorSchemaPath) {
    args.push('--connector-schema', job.settings.connectorSchemaPath);
  }
  if (job.settings.model) {
    args.push('--model', job.settings.model);
  }
  if (job.settings.m365Tenant) {
    args.push('--m365-tenant', job.settings.m365Tenant);
  }

  setJobStatus(job, 'running', 'generate', 'Generating evaluation questions and expected answers...');
  await runCommand(job, 'Running EvalGen...', nodeCommand, args, { runAsNode: isElectronRuntime });

  const rows = fs.existsSync(outputCsv) ? parseCsv(fs.readFileSync(outputCsv, 'utf8')).rows : [];
  job.summary.generatedRows = rows.length;
  setJobStatus(job, 'generated', 'review', `Generation complete. ${rows.length} evaluation(s) are ready to review.`, {
    generatedRows: rows.length,
  });
}

function failJob(job, error) {
  setJobStatus(job, 'failed', job.phase || 'failed', error.message);
  emit(job, 'error', error.stack || error.message);
}

function parseCsv(text) {
  const records = [];
  let row = [];
  let value = '';
  let inQuotes = false;

  for (let index = 0; index < text.length; index += 1) {
    const char = text[index];
    const next = text[index + 1];

    if (inQuotes) {
      if (char === '"' && next === '"') {
        value += '"';
        index += 1;
      } else if (char === '"') {
        inQuotes = false;
      } else {
        value += char;
      }
      continue;
    }

    if (char === '"') {
      inQuotes = true;
    } else if (char === ',') {
      row.push(value);
      value = '';
    } else if (char === '\n') {
      row.push(value);
      records.push(row);
      row = [];
      value = '';
    } else if (char !== '\r') {
      value += char;
    }
  }

  if (value.length > 0 || row.length > 0) {
    row.push(value);
    records.push(row);
  }

  if (records.length === 0) {
    return { headers: [], rows: [] };
  }

  const headers = records[0].map((header) => header.trim());
  const rows = records.slice(1)
    .filter((record) => record.some((cell) => cell.trim().length > 0))
    .map((record) => {
      const item = {};
      headers.forEach((header, index) => {
        item[header] = record[index] || '';
      });
      return item;
    });

  return { headers, rows };
}

function stringifyCsv(headers, rows) {
  const escapeCell = (value) => {
    const text = value == null ? '' : String(value);
    return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
  };
  const lines = [headers.map(escapeCell).join(',')];
  for (const row of rows) {
    lines.push(headers.map((header) => escapeCell(row[header])).join(','));
  }
  return `${lines.join('\r\n')}\r\n`;
}

function evalRows(job) {
  if (!job.paths.outputCsv || !fs.existsSync(job.paths.outputCsv)) {
    return { headers: [], rows: [] };
  }
  return parseCsv(fs.readFileSync(job.paths.outputCsv, 'utf8'));
}

async function saveEvalRows(job, req, res) {
  if (!job.paths.outputCsv) {
    sendError(res, 404, 'No generated evaluation CSV was found for this job.');
    return;
  }

  const payload = await readJsonBody(req);
  const rows = Array.isArray(payload.rows) ? payload.rows : [];
  const existing = evalRows(job);
  const preferred = ['prompt', 'expected_answer', 'source_location', 'actual_answer', 'similarity_score'];
  const headers = [
    ...preferred.filter((header) => existing.headers.includes(header) || rows.some((row) => Object.prototype.hasOwnProperty.call(row, header))),
    ...existing.headers.filter((header) => !preferred.includes(header)),
  ];
  for (const row of rows) {
    for (const header of Object.keys(row)) {
      if (!headers.includes(header)) headers.push(header);
    }
  }

  const tempPath = `${job.paths.outputCsv}.tmp`;
  fs.writeFileSync(tempPath, stringifyCsv(headers, rows), 'utf8');
  fs.renameSync(tempPath, job.paths.outputCsv);
  job.summary.generatedRows = rows.length;
  saveJob(job);
  emit(job, 'log', `Saved ${rows.length} edited evaluation row(s).`);
  sendJson(res, 200, { ok: true, rowCount: rows.length });
}

async function runScore(job, settings) {
  if (!job.paths.outputCsv || !fs.existsSync(job.paths.outputCsv)) {
    throw new Error('Generate and save an evaluation set before scoring.');
  }

  setJobStatus(job, 'running', 'setup', 'Checking local scoring tools...');
  await ensureTool(job, 'eval-score');

  const paths = toolPaths();
  fs.mkdirSync(job.paths.scoreOutputDir, { recursive: true });
  const args = [
    paths.evalScoreJs,
    '--input', job.paths.outputCsv,
    '--output-dir', job.paths.scoreOutputDir,
    '--threshold', String(Math.min(100, Math.max(0, Number(settings.threshold || 70)))),
  ];

  if (job.paths.sidecar && fs.existsSync(job.paths.sidecar)) {
    args.push('--sidecar', job.paths.sidecar);
  }
  if (settings.connectorId) {
    args.push('--connector-id', String(settings.connectorId));
  }
  if (settings.tenantId) {
    args.push('--tenant-id', String(settings.tenantId));
  }
  if (settings.systemPrompt) {
    args.push('--system-prompt', String(settings.systemPrompt));
  }

  setJobStatus(job, 'running', 'score', 'Running EvalScore against Microsoft 365 Copilot / WorkIQ...');
  await runCommand(job, 'Running EvalScore...', nodeCommand, args, { acceptedCodes: [0, 1], runAsNode: isElectronRuntime });

  const basename = path.basename(job.paths.outputCsv, path.extname(job.paths.outputCsv));
  job.paths.scoredCsv = path.join(job.paths.scoreOutputDir, `${basename}-results.csv`);
  job.paths.report = path.join(job.paths.scoreOutputDir, `${basename}-report.md`);
  job.summary.scoredRows = fs.existsSync(job.paths.scoredCsv) ? parseCsv(fs.readFileSync(job.paths.scoredCsv, 'utf8')).rows.length : 0;
  saveJob(job);
  setJobStatus(job, 'scored', 'complete', 'Scoring complete. Results and report are ready.', {
    scoredRows: job.summary.scoredRows,
  });
}

function fileFor(job, name) {
  const known = {
    csv: job.paths.outputCsv,
    sidecar: job.paths.sidecar,
    review: job.paths.review,
    diagnostics: job.paths.diagnostics,
    scoredCsv: job.paths.scoredCsv,
    report: job.paths.report,
    log: eventsPath(job),
  };
  const filePath = known[name];
  if (!filePath || !fs.existsSync(filePath)) return undefined;
  return filePath;
}

function sendDownload(res, filePath) {
  res.writeHead(200, {
    'Content-Type': 'application/octet-stream',
    'Content-Disposition': `attachment; filename="${path.basename(filePath).replace(/"/g, '')}"`,
  });
  fs.createReadStream(filePath).pipe(res);
}

function contentTypeFor(filePath) {
  const extension = path.extname(filePath).toLowerCase();
  if (extension === '.csv') return 'text/csv; charset=utf-8';
  if (extension === '.md') return 'text/plain; charset=utf-8';
  if (extension === '.json') return 'application/json; charset=utf-8';
  return 'text/plain; charset=utf-8';
}

function sendInlineFile(res, filePath) {
  res.writeHead(200, {
    'Content-Type': contentTypeFor(filePath),
    'Content-Disposition': `inline; filename="${path.basename(filePath).replace(/"/g, '')}"`,
    'X-Content-Type-Options': 'nosniff',
  });
  fs.createReadStream(filePath).pipe(res);
}

function openFolder(job) {
  const folder = job.dir;
  if (process.platform === 'win32') {
    childProcess.spawn('explorer.exe', [folder], { detached: true, stdio: 'ignore' }).unref();
  } else if (process.platform === 'darwin') {
    childProcess.spawn('open', [folder], { detached: true, stdio: 'ignore' }).unref();
  } else {
    childProcess.spawn('xdg-open', [folder], { detached: true, stdio: 'ignore' }).unref();
  }
}

function openUrl(url) {
  if (process.platform === 'win32') {
    childProcess.spawn('cmd', ['/c', 'start', '', url], { detached: true, stdio: 'ignore' }).unref();
  } else if (process.platform === 'darwin') {
    childProcess.spawn('open', [url], { detached: true, stdio: 'ignore' }).unref();
  } else {
    childProcess.spawn('xdg-open', [url], { detached: true, stdio: 'ignore' }).unref();
  }
}

function checkHealth(port) {
  return new Promise((resolve) => {
    const req = http.get({
      host: HOST,
      port,
      path: '/api/health',
      timeout: 750,
    }, (response) => {
      response.resume();
      resolve(response.statusCode === 200);
    });
    req.on('timeout', () => {
      req.destroy();
      resolve(false);
    });
    req.on('error', () => resolve(false));
  });
}

async function findExistingServer() {
  const candidatePorts = new Set();
  const portFile = path.join(runtimeDir, 'port.json');
  if (fs.existsSync(portFile)) {
    try {
      const metadata = JSON.parse(fs.readFileSync(portFile, 'utf8'));
      if (Number(metadata.port)) {
        candidatePorts.add(Number(metadata.port));
      }
    } catch {
      // Ignore stale or unreadable runtime metadata.
    }
  }

  for (let offset = 0; offset < MAX_PORT_ATTEMPTS; offset += 1) {
    candidatePorts.add(DEFAULT_PORT + offset);
  }

  for (const port of candidatePorts) {
    if (await checkHealth(port)) {
      return `http://${HOST}:${port}`;
    }
  }
  return undefined;
}

async function routeApi(req, res, pathname) {
  if (!checkLocalOrigin(req, res)) return true;

  if (req.method === 'GET' && pathname === '/api/health') {
    sendJson(res, 200, { ok: true, port: serverPort, repoRoot, workspaceDir });
    return true;
  }

  if (req.method === 'GET' && pathname === '/api/jobs') {
    const jobs = Array.from(activeJobs.values())
      .sort((a, b) => String(b.createdAt).localeCompare(String(a.createdAt)))
      .map(publicJob);
    sendJson(res, 200, { jobs });
    return true;
  }

  if (req.method === 'POST' && pathname === '/api/generate') {
    await handleGenerateUpload(req, res);
    return true;
  }

  const match = pathname.match(/^\/api\/jobs\/([^/]+)(?:\/([^/]+))?(?:\/([^/]+))?$/);
  if (!match) return false;

  const job = getJob(match[1]);
  if (!job) {
    sendError(res, 404, 'Job was not found.');
    return true;
  }
  const action = match[2];
  const subAction = match[3];

  if (req.method === 'GET' && !action) {
    sendJson(res, 200, { job: publicJob(job) });
    return true;
  }

  if (req.method === 'GET' && action === 'events') {
    const lastEventId = Number(req.headers['last-event-id'] || 0);
    res.writeHead(200, {
      'Content-Type': 'text/event-stream; charset=utf-8',
      'Cache-Control': 'no-store',
      Connection: 'keep-alive',
    });
    for (const event of job.events.filter((item) => item.id > lastEventId)) {
      writeSse(res, event);
    }
    const heartbeat = setInterval(() => res.write(': ping\n\n'), 15000);
    job.subscribers.add(res);
    req.on('close', () => {
      clearInterval(heartbeat);
      job.subscribers.delete(res);
    });
    return true;
  }

  if (req.method === 'GET' && action === 'evals') {
    sendJson(res, 200, evalRows(job));
    return true;
  }

  if (req.method === 'POST' && action === 'evals') {
    try {
      await saveEvalRows(job, req, res);
    } catch (error) {
      sendError(res, 400, error.message);
    }
    return true;
  }

  if (req.method === 'POST' && action === 'score') {
    try {
      const settings = await readJsonBody(req);
      setJobStatus(job, 'queued', 'score', 'Starting scoring...');
      sendJson(res, 202, { job: publicJob(job) });
      runScore(job, settings).catch((error) => failJob(job, error));
    } catch (error) {
      sendError(res, 400, error.message);
    }
    return true;
  }

  if (req.method === 'POST' && action === 'open-folder') {
    openFolder(job);
    sendJson(res, 200, { ok: true });
    return true;
  }

  if (req.method === 'GET' && action === 'files' && subAction) {
    const target = fileFor(job, subAction);
    if (!target) {
      sendError(res, 404, 'Requested file is not available yet.');
      return true;
    }
    sendDownload(res, target);
    return true;
  }

  if (req.method === 'GET' && action === 'view' && subAction) {
    const target = fileFor(job, subAction);
    if (!target) {
      sendError(res, 404, 'Requested file is not available yet.');
      return true;
    }
    sendInlineFile(res, target);
    return true;
  }

  sendError(res, 404, 'API endpoint was not found.');
  return true;
}

const server = http.createServer(async (req, res) => {
  try {
    const requestUrl = new URL(req.url || '/', `http://${HOST}:${serverPort}`);
    if (requestUrl.pathname.startsWith('/api/')) {
      const handled = await routeApi(req, res, requestUrl.pathname);
      if (handled) return;
    }
    serveStatic(req, res, requestUrl.pathname);
  } catch (error) {
    console.error(error);
    sendError(res, 500, error.message);
  }
});

function listenWithFallback(port, attemptsRemaining, options = {}) {
  return new Promise((resolve, reject) => {
    const onError = (error) => {
      if (error.code === 'EADDRINUSE' && attemptsRemaining > 1) {
        listenWithFallback(port + 1, attemptsRemaining - 1, options).then(resolve, reject);
        return;
      }
      console.error(`Eval UI could not start: ${error.message}`);
      if (options.exitOnError) {
        process.exit(1);
      }
      reject(error);
    };

    server.once('error', onError);
    server.listen(port, HOST, () => {
      server.off('error', onError);
      const address = server.address();
      serverPort = typeof address === 'object' && address ? address.port : port;
      const url = `http://${HOST}:${serverPort}`;
      fs.writeFileSync(path.join(runtimeDir, 'port.json'), JSON.stringify({ url, port: serverPort, startedAt: nowIso() }, null, 2));
      console.log(`Eval UI is running at ${url}`);
      console.log('Keep this window open while you use the UI.');
      if (options.open) {
        openUrl(url);
      }
      resolve({ url, port: serverPort, server });
    });
  });
}

function stop() {
  for (const job of activeJobs.values()) {
    if (job.activeChild && !job.activeChild.killed) {
      job.activeChild.kill();
      job.activeChild = null;
    }
  }

  return new Promise((resolve, reject) => {
    if (!server.listening) {
      resolve();
      return;
    }
    server.close((error) => {
      if (error) {
        reject(error);
        return;
      }
      resolve();
    });
  });
}

async function start(options = {}) {
  const open = options.open ?? process.argv.includes('--open');
  const port = options.port ?? DEFAULT_PORT;
  const attempts = port === 0 ? 1 : MAX_PORT_ATTEMPTS;

  if (open) {
    const existingUrl = await findExistingServer();
    if (existingUrl) {
      console.log(`Eval UI is already running at ${existingUrl}`);
      openUrl(existingUrl);
      return { url: existingUrl, port: new URL(existingUrl).port, server };
    }
  }

  loadJobs();
  return listenWithFallback(port, attempts, { open, exitOnError: options.exitOnError ?? require.main === module });
}

if (require.main === module) {
  start().catch((error) => {
    console.error(`Eval UI could not start: ${error.message}`);
    process.exit(1);
  });
}

module.exports = { start, stop, server };
