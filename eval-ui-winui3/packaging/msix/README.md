# MSIX packaging assets

Empty until the **phase C `msix-packaging` todo** lands. Will contain:

- `Package.appxmanifest` extras (FTAs for app-owned extensions only,
  jump-list activation hooks — see plan Section 7).
- PRI resource extras (icons, scale-200 / -400 assets).
- Signing helper scripts (Azure Trusted Signing in prod; self-signed
  PFX wiring for nightly).

This file exists so the directory survives a clone (git does not track
empty directories).
