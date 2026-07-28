# VDict repository guidance

VDict is a local Windows MDX/MDD dictionary reader built with .NET 10, WinUI 3,
WebView2, SQLite, and PowerShell automation.

## Architecture

- `VDict.Core` contains platform-independent models, contracts, and policies.
- `VDict.Infrastructure` implements persistence, dictionary formats, indexes,
  catalogs, caching, logging, and maintenance.
- `VDict.Presentation` contains UI-independent view models and platform ports.
- `VDict.WinUI` is the composition root and Windows/WebView2 adapter.
- Dependencies flow inward; Core and Presentation must not reference WinUI or
  concrete WebView2 UI types.

See `docs/ARCHITECTURE.md` for the detailed boundaries and data flow.

## Required checks

```powershell
dotnet restore VDict.sln
dotnet build VDict.sln --configuration Release --no-restore
dotnet test VDict.sln --configuration Release --no-build
.\build\Test-LocalizationResources.ps1
```

Run `.\build\Run-RemediationPhase0A.ps1` when changing MDX/MDD parsing, resource
resolution, indexing, or deterministic fixture behavior.

## Working rules

- Work on a task branch and preserve unrelated user changes.
- Do not merge, push, publish, or delete branches without explicit authorization.
- Keep generated artifacts, user data, real dictionaries, paths, logs, secrets,
  and signing material out of version control.
- Update the closest module README and public documentation when behavior,
  architecture, commands, configuration, or release output changes.
- Prefer deterministic tests and synthetic fixtures over real user data.
