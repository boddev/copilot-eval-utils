const path = require('path');
const { app, BrowserWindow } = require('electron');

let mainWindow;
let serverHandle;
let stopping = false;

function toolsRoot() {
  return app.isPackaged ? process.resourcesPath : path.resolve(__dirname, '..');
}

function configureServerEnvironment() {
  const userData = app.getPath('userData');
  process.env.EVAL_UI_WORKSPACE_DIR = path.join(userData, 'workspace');
  process.env.EVAL_UI_RUNTIME_DIR = path.join(userData, '.runtime');
  process.env.EVAL_UI_TOOLS_ROOT = toolsRoot();
  process.env.EVAL_UI_PACKAGED = app.isPackaged ? '1' : '0';
}

async function createWindow() {
  if (!serverHandle) {
    configureServerEnvironment();
    const { start } = require('./server');
    serverHandle = await start({ open: false, port: 0, exitOnError: false });
  }

  mainWindow = new BrowserWindow({
    width: 1280,
    height: 900,
    minWidth: 960,
    minHeight: 700,
    show: false,
    webPreferences: {
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });

  mainWindow.once('ready-to-show', () => {
    mainWindow.show();
  });
  mainWindow.on('closed', () => {
    mainWindow = undefined;
  });

  await mainWindow.loadURL(serverHandle.url);
}

const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (!mainWindow) return;
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.focus();
  });

  app.whenReady().then(createWindow);

  app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') {
      app.quit();
    }
  });

  app.on('before-quit', async (event) => {
    if (!serverHandle || stopping) return;
    event.preventDefault();
    stopping = true;
    const { stop } = require('./server');
    serverHandle = undefined;
    try {
      await stop();
    } finally {
      app.quit();
    }
  });

  app.on('activate', () => {
    if (!mainWindow) {
      createWindow();
    }
  });
}
