# RevitFlux

An MCP server and add-in that lets AI assistants work with Autodesk Revit.

**Based on [RevitCortex](https://github.com/LuDattilo/RevitCortex) by Luigi Dattilo (MIT).** See [NOTICE.md](NOTICE.md).

## Changes from RevitCortex v1.0.50

- Tool calls queue instead of failing with "previous Revit event is still pending".
- Timeout errors say whether the tool never ran or is still running.
- A confirmation clicked after a timeout no longer runs the operation.
- Script modes `manual` and `readonly` work; unknown modes are rejected.
- Scripts get 280 seconds, including confirmation time.
- New tool: `create_filled_regions` (batch, one undo step).
- Tests no longer write to the user's audit log.
- Connection failures return a clear message (for example, when the Revit switch is off).
- Upstream update checks, support address and Azure app id are removed.

## Side by side with RevitCortex

| | RevitCortex | RevitFlux |
|---|---|---|
| Port | 8080 | 8090 |
| Power BI visual port | 27016 | 27026 |
| Data folder | `~/.revitcortex` | `~/.revitflux` |
| Port variable | `REVITCORTEX_PORT` | `REVITFLUX_PORT` |

Add-in id, assembly names and storage schema also differ. The RevitCortex Power BI visual does not connect to RevitFlux.

## Build, test, deploy

```powershell
dotnet test src/RevitFlux.Tests/RevitFlux.Tests.csproj -c "Debug R26"
./deploy.ps1 -RevitVersion 2024
```

`deploy.ps1` installs for the current user only, and refuses to run while Revit or the RevitFlux server is open.
Code execution is off by default; enable it in RevitFlux Settings.
Use `-ServerOnly` to update just the MCP server while Revit stays open.

## Status

Early version (0.1.0). Loaded and tested live in Revit 2024; Revit 2023, 2025 and 2026 builds compile but have not been tested in Revit. Try it on a copy of a model first.
