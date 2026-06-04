# Portable / unsigned ZIP packaging assets

Empty until the **phase C `msix-packaging` todo** lands. Will contain
the layout descriptor + helper scripts for the secondary, unpackaged
distribution that's built from the same `dotnet publish -r win-x64
--self-contained true` output as the MSIX.

Note the portable build is documented as a **reduced-capability
fallback** (no MSIX-bound file associations, no package-identity jump
list, restricted toast behavior). See plan Section 7.

This file exists so the directory survives a clone (git does not track
empty directories).
