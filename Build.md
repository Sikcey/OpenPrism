# Build OpenPrism

On Windows x64, install Git, Go, the .NET 10 SDK and the Rust toolchain. An internet connection is needed to fetch pinned upstream source and dependencies.

From the repository root in PowerShell, run:

```powershell
.\scripts\prepare-update.ps1
```

The script builds and validates a matched development package under `artifacts/update-candidate-<timestamp>/package`. Keep the UI, backend and packet engine from the same package together. The development package is unsigned. See [installation](docs/installation.md) for installation instructions.
