@echo off
setlocal

cd /d "%~dp0"
echo.
echo Starting Eval UI...
echo.

where node >nul 2>nul
if errorlevel 1 (
  echo Node.js is required to run Eval UI.
  echo Please install Node.js LTS from https://nodejs.org/ and then run this file again.
  echo.
  pause
  exit /b 1
)

if not exist "eval-ui\package.json" (
  echo The eval-ui folder was not found next to this starter file.
  echo Make sure you are running this from the EvaluationCLI project root.
  echo.
  pause
  exit /b 1
)

cd /d "%~dp0eval-ui"

if not exist "node_modules\busboy\package.json" (
  echo Installing Eval UI dependencies. This happens once and can take a few minutes.
  npm install
  if errorlevel 1 (
    echo.
    echo Eval UI setup failed. Please check the messages above.
    pause
    exit /b 1
  )
)

node server.js --open
if errorlevel 1 (
  echo.
  echo Eval UI stopped because of an error.
  pause
  exit /b 1
)
