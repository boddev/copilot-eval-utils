const state = {
  files: [],
  job: null,
  events: null,
  rows: [],
  dirty: false,
};

const elements = {
  connectionStatus: document.getElementById('connectionStatus'),
  workspaceText: document.getElementById('workspaceText'),
  chooseFilesButton: document.getElementById('chooseFilesButton'),
  chooseFolderButton: document.getElementById('chooseFolderButton'),
  fileInput: document.getElementById('fileInput'),
  folderInput: document.getElementById('folderInput'),
  selectedFiles: document.getElementById('selectedFiles'),
  description: document.getElementById('description'),
  count: document.getElementById('count'),
  extensions: document.getElementById('extensions'),
  provider: document.getElementById('provider'),
  model: document.getElementById('model'),
  m365Tenant: document.getElementById('m365Tenant'),
  connectorSchema: document.getElementById('connectorSchema'),
  generateButton: document.getElementById('generateButton'),
  progressPanel: document.getElementById('progressPanel'),
  progressBar: document.getElementById('progressBar'),
  progressMessage: document.getElementById('progressMessage'),
  logOutput: document.getElementById('logOutput'),
  fileLinks: document.getElementById('fileLinks'),
  viewReview: document.getElementById('viewReview'),
  viewCsv: document.getElementById('viewCsv'),
  downloadCsv: document.getElementById('downloadCsv'),
  downloadReview: document.getElementById('downloadReview'),
  openFolderButton: document.getElementById('openFolderButton'),
  reviewPanel: document.getElementById('reviewPanel'),
  saveButton: document.getElementById('saveButton'),
  saveStatus: document.getElementById('saveStatus'),
  evalEditor: document.getElementById('evalEditor'),
  scorePanel: document.getElementById('scorePanel'),
  connectorId: document.getElementById('connectorId'),
  scoreTenantId: document.getElementById('scoreTenantId'),
  threshold: document.getElementById('threshold'),
  systemPrompt: document.getElementById('systemPrompt'),
  scoreButton: document.getElementById('scoreButton'),
  scoreResults: document.getElementById('scoreResults'),
};

async function api(path, options = {}) {
  const response = await fetch(path, options);
  const contentType = response.headers.get('content-type') || '';
  const payload = contentType.includes('application/json') ? await response.json() : await response.text();
  if (!response.ok) {
    throw new Error(payload.error || payload || `Request failed with status ${response.status}`);
  }
  return payload;
}

function setConnected(ok, detail) {
  elements.connectionStatus.textContent = ok ? 'Connected' : 'Disconnected';
  elements.connectionStatus.className = ok ? 'pill pill-good' : 'pill pill-bad';
  elements.workspaceText.textContent = detail;
}

function setFiles(files) {
  state.files = Array.from(files || []);
  if (state.files.length === 0) {
    elements.selectedFiles.textContent = 'No dataset selected yet.';
    elements.selectedFiles.classList.add('empty');
    return;
  }

  const preview = state.files
    .slice(0, 5)
    .map((file) => file.webkitRelativePath || file.name)
    .join('\n');
  const suffix = state.files.length > 5 ? `\n...and ${state.files.length - 5} more` : '';
  elements.selectedFiles.textContent = `${state.files.length} file(s) selected:\n${preview}${suffix}`;
  elements.selectedFiles.classList.remove('empty');
}

function showProgress(message) {
  elements.progressPanel.classList.remove('hidden');
  elements.progressMessage.textContent = message;
  setProgressState('running');
}

function setProgressState(status) {
  elements.progressBar.classList.remove('is-running', 'is-complete', 'is-failed');
  elements.progressBar.classList.add(`is-${status}`);
}

function appendLog(message) {
  elements.logOutput.textContent += message.endsWith('\n') ? message : `${message}\n`;
  elements.logOutput.scrollTop = elements.logOutput.scrollHeight;
}

function setDirty(dirty) {
  state.dirty = dirty;
  elements.saveStatus.textContent = dirty ? 'Unsaved changes.' : 'All edits saved.';
}

function updateJobLinks() {
  if (!state.job) return;
  elements.fileLinks.classList.remove('hidden');
  elements.viewReview.href = `/api/jobs/${state.job.id}/view/review`;
  elements.viewCsv.href = `/api/jobs/${state.job.id}/view/csv`;
  elements.downloadCsv.href = `/api/jobs/${state.job.id}/files/csv`;
  elements.downloadReview.href = `/api/jobs/${state.job.id}/files/review`;
}

function connectEvents(jobId) {
  if (state.events) {
    state.events.close();
  }

  state.events = new EventSource(`/api/jobs/${jobId}/events`);
  state.events.addEventListener('status', async (event) => {
    const payload = JSON.parse(event.data);
    const { status, phase } = payload.data;
    elements.progressMessage.textContent = payload.message;
    state.job = { ...state.job, status, phase };
    const progressState = status === 'failed'
      ? 'failed'
      : status === 'generated' || status === 'scored'
        ? 'complete'
        : 'running';
    setProgressState(progressState);

    if (status === 'generated') {
      elements.generateButton.disabled = false;
      updateJobLinks();
      await loadEvals();
    }

    if (status === 'scored') {
      elements.scoreButton.disabled = false;
      updateJobLinks();
      await refreshJob();
      showScoreResults();
    }

    if (status === 'failed') {
      elements.generateButton.disabled = false;
      elements.scoreButton.disabled = false;
      appendLog(`ERROR: ${payload.message}`);
    }
  });

  state.events.addEventListener('log', (event) => {
    const payload = JSON.parse(event.data);
    appendLog(payload.message);
  });

  state.events.addEventListener('error', (event) => {
    if (event.data) {
      const payload = JSON.parse(event.data);
      appendLog(payload.message);
    }
  });
}

async function refreshJob() {
  if (!state.job) return;
  const payload = await api(`/api/jobs/${state.job.id}`);
  state.job = payload.job;
}

async function startGenerate() {
  if (state.files.length === 0) {
    alert('Choose one or more dataset files first.');
    return;
  }

  if (!elements.description.value.trim()) {
    alert('Add a short description of what the dataset contains.');
    elements.description.focus();
    return;
  }

  const formData = new FormData();
  for (const file of state.files) {
    formData.append('dataset', file, file.webkitRelativePath || file.name);
  }
  const schemaFile = elements.connectorSchema.files[0];
  if (schemaFile) {
    formData.append('connectorSchema', schemaFile, schemaFile.name);
  }
  formData.append('description', elements.description.value.trim());
  formData.append('count', elements.count.value);
  formData.append('extensions', elements.extensions.value);
  formData.append('provider', elements.provider.value);
  formData.append('model', elements.model.value.trim());
  formData.append('m365Tenant', elements.m365Tenant.value.trim());

  elements.generateButton.disabled = true;
  elements.logOutput.textContent = '';
  elements.reviewPanel.classList.add('hidden');
  elements.scorePanel.classList.add('hidden');
  elements.scoreResults.classList.add('hidden');
  elements.fileLinks.classList.add('hidden');
  showProgress('Uploading dataset...');

  try {
    const payload = await api('/api/generate', {
      method: 'POST',
      body: formData,
    });
    state.job = payload.job;
    connectEvents(state.job.id);
    appendLog(`Started job ${state.job.id}.`);
  } catch (error) {
    elements.generateButton.disabled = false;
    elements.progressMessage.textContent = error.message;
    setProgressState('failed');
    appendLog(`ERROR: ${error.message}`);
  }
}

function renderEditor(headers) {
  elements.evalEditor.innerHTML = '';
  const displayHeaders = ['prompt', 'expected_answer', 'source_location', 'actual_answer']
    .filter((header) => headers.includes(header));

  state.rows.forEach((row, index) => {
    const card = document.createElement('article');
    card.className = 'eval-card';
    const title = document.createElement('h3');
    title.textContent = `Evaluation ${index + 1}`;
    card.appendChild(title);

    for (const header of displayHeaders) {
      const label = document.createElement('label');
      label.textContent = header.replace(/_/g, ' ');
      const textarea = document.createElement('textarea');
      textarea.rows = header === 'prompt' ? 3 : 4;
      textarea.value = row[header] || '';
      textarea.addEventListener('input', () => {
        row[header] = textarea.value;
        setDirty(true);
      });
      card.appendChild(label);
      card.appendChild(textarea);
    }

    elements.evalEditor.appendChild(card);
  });
}

async function loadEvals() {
  if (!state.job) return;
  const payload = await api(`/api/jobs/${state.job.id}/evals`);
  state.rows = payload.rows;
  renderEditor(payload.headers);
  elements.reviewPanel.classList.remove('hidden');
  elements.scorePanel.classList.remove('hidden');
  setDirty(false);
}

async function saveEvals() {
  if (!state.job) return;
  elements.saveButton.disabled = true;
  elements.saveStatus.textContent = 'Saving...';
  try {
    await api(`/api/jobs/${state.job.id}/evals`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ rows: state.rows }),
    });
    setDirty(false);
  } catch (error) {
    elements.saveStatus.textContent = error.message;
  } finally {
    elements.saveButton.disabled = false;
  }
}

async function startScore() {
  if (!state.job) return;
  if (state.dirty) {
    await saveEvals();
    if (state.dirty) return;
  }

  elements.scoreButton.disabled = true;
  showProgress('Starting scoring...');
  connectEvents(state.job.id);
  try {
    const payload = await api(`/api/jobs/${state.job.id}/score`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        connectorId: elements.connectorId.value.trim(),
        tenantId: elements.scoreTenantId.value.trim(),
        threshold: elements.threshold.value,
        systemPrompt: elements.systemPrompt.value.trim(),
      }),
    });
    state.job = payload.job;
    appendLog('Scoring started.');
  } catch (error) {
    elements.scoreButton.disabled = false;
    setProgressState('failed');
    appendLog(`ERROR: ${error.message}`);
  }
}

function showScoreResults() {
  if (!state.job) return;
  elements.scoreResults.classList.remove('hidden');
  elements.scoreResults.innerHTML = `
    <strong>Scoring complete.</strong>
    <p>${state.job.summary?.scoredRows || 0} row(s) were written to the scored results file.</p>
    <div class="button-row">
      <a class="button secondary" href="/api/jobs/${state.job.id}/view/report" target="_blank" rel="noopener">View report</a>
      <a class="button secondary" href="/api/jobs/${state.job.id}/view/scoredCsv" target="_blank" rel="noopener">View scored CSV</a>
      <a class="button secondary" href="/api/jobs/${state.job.id}/files/scoredCsv">Download scored CSV</a>
      <a class="button secondary" href="/api/jobs/${state.job.id}/files/report">Download report</a>
    </div>
  `;
}

async function openOutputFolder() {
  if (!state.job) return;
  await api(`/api/jobs/${state.job.id}/open-folder`, { method: 'POST' });
}

function bindEvents() {
  elements.chooseFilesButton.addEventListener('click', () => elements.fileInput.click());
  elements.chooseFolderButton.addEventListener('click', () => elements.folderInput.click());
  elements.fileInput.addEventListener('change', () => setFiles(elements.fileInput.files));
  elements.folderInput.addEventListener('change', () => setFiles(elements.folderInput.files));
  elements.generateButton.addEventListener('click', startGenerate);
  elements.saveButton.addEventListener('click', saveEvals);
  elements.scoreButton.addEventListener('click', startScore);
  elements.openFolderButton.addEventListener('click', openOutputFolder);
}

async function boot() {
  bindEvents();
  try {
    const health = await api('/api/health');
    setConnected(true, `Workspace: ${health.workspaceDir}`);
  } catch (error) {
    setConnected(false, error.message);
  }
}

boot();
