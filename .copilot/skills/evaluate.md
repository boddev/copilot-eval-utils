---
name: evaluate
description: Run a prompt evaluation against WorkIQ (Microsoft 365 Copilot) and produce a scored report
---

# Evaluate WorkIQ Prompts

This skill evaluates how well WorkIQ (Microsoft 365 Copilot) answers a set of predefined questions by comparing responses against expected answers.

## How to Use

When the user wants to evaluate WorkIQ prompts, follow this process:

1. **Identify the evaluation file** — Ask the user for the path to their evaluation dataset (CSV, TSV, XLSX, or JSON). The file should have these columns: `prompt`, `expected_answer`, `source_location`, `actual_answer` (can be blank).

2. **Identify the system prompt** (optional) — Ask if they want to provide a system prompt (inline or as a file path).

3. **Run EvalScore** (Node.js command):
   ```bash
   eval-score --input <path-to-eval-file> [--system-prompt "..."] [--system-prompt-file <path>] [--output-dir <dir>] [--threshold <0-100>] [--tenant-id <id>]
   ```

   Or use the PowerShell implementation directly:
   ```powershell
   cd C:\Users\bodonnell\src\EvaluationCLI\eval-score\powershell
   .\Invoke-Evaluation.ps1 -InputFile <path-to-eval-file> [-SystemPrompt "..."] [-SystemPromptFile <path>] [-OutputDir <dir>] [-Threshold <0-100>] [-TenantId <id>]
   ```

4. **The tool calls WorkIQ directly** — Each prompt is sent through the local WorkIQ CLI/MCP integration. No intermediary files or agent processing is needed.

5. **Report results** — Once complete, the tool produces:
   - A completed evaluation file with actual answers and similarity scores
   - A markdown report with summary statistics and per-question details

## Parameters

| Parameter | Required | Description |
|-----------|----------|-------------|
| `--input` / `-InputFile` | Yes | Path to evaluation dataset (CSV, TSV, XLSX, or JSON) |
| `--system-prompt` / `-SystemPrompt` | No | System prompt to prepend to each question |
| `--system-prompt-file` / `-SystemPromptFile` | No | Path to a file containing the system prompt |
| `--output-dir` / `-OutputDir` | No | Output directory (default: `./output`) |
| `--threshold` / `-Threshold` | No | Pass/fail score threshold 0-100 (default: 70) |
| `--tenant-id` / `-TenantId` | No | Microsoft 365 tenant ID to target |
| `--setup` / `-Setup` | No | Run preflight checks only |
| `--skip-preflight` / `-SkipPreflight` | No | Skip preflight checks |
