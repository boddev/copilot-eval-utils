# EvalToolkit (WinUI 3) — Documentation

Documentation for the Windows-native EvalToolkit, a WinUI 3 / .NET 10
companion to the Electron-based [Eval UI](../../eval-ui/README.md).

| If you want to… | Read |
|---|---|
| Walk through the app: home, wizard, jobs, file activations, jump list, diagnostics view | [user-guide.md](user-guide.md) |
| Run EvalToolkit headlessly (CI smoke tests, diagnostics dumps) | [cli-reference.md](cli-reference.md) |
| Diagnose install failures, blank windows, stale jump list, auth loops | [troubleshooting.md](troubleshooting.md) |
| Understand the EvalGen / EvalScore file formats this app reads and writes | [file-formats.md](file-formats.md) |
| Build, test, package, and sign the app locally | [developer-guide.md](developer-guide.md) |
| Configure the repo for CI release signing via Azure Trusted Signing | [ci-release-setup.md](ci-release-setup.md) |

The application source lives under [`../src/EvalToolkit.UI/`](../src/EvalToolkit.UI/);
shared engines (the C# ports of EvalGen and EvalScore) live under
[`../src/EvalToolkit.EvalGen/`](../src/EvalToolkit.EvalGen/) and
[`../src/EvalToolkit.EvalScore/`](../src/EvalToolkit.EvalScore/).
