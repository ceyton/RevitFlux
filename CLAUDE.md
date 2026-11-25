# RevitFlux

MCP server + Revit add-in. Based on RevitCortex (MIT): keep LICENSE and NOTICE.md.

- `src/RevitFlux.Core` shared types (netstandard2.0) · `Plugin` add-in, ribbon, socket, queue · `Tools` Revit tools · `Server` MCP stdio server (net8) · `Tests` xUnit
- Build configs: `Debug|Release R23..R27`. R23/R24 = net48, R25/R26 = net8, R27 = net10. Always build R24 and R26.
- Test: `dotnet test src/RevitFlux.Tests/RevitFlux.Tests.csproj -c "Debug R24"`
- Install: `./deploy.ps1` (Revit closed) or `./deploy.ps1 -ServerOnly`.

## Rules

- Tokens first. Every listed tool, description and response field costs tokens in every session.
- A new tool needs an `ICortexTool` in Tools and an `[McpServerTool]` wrapper in Server. It is found through `find_tools`; add it to `ToolCatalog.Core` only if most sessions need it.
- Server wrappers return `result.ToLean()`, never `ToString()`.
- Destructive tools: `dryRun` option plus `session.RequestConfirmation`.
- Never return a truncated list without saying so; counters must stay true.
- `send_code_to_revit` stays off by default, sandboxed and confirmed.
- All text in English.
