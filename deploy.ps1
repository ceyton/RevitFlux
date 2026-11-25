# Installs RevitFlux for the current user. Touches only RevitFlux paths.
# -ServerOnly updates just the MCP server, so Revit can stay open.
param([ValidateSet('2023','2024','2025','2026')][string]$RevitVersion = '2024', [switch]$ServerOnly)
$ErrorActionPreference = 'Stop'
$blockers = @('RevitFlux.Server'); if (-not $ServerOnly) { $blockers += 'Revit' }
if (Get-Process $blockers -ErrorAction SilentlyContinue) {
    throw 'Close Revit and any assistant using RevitFlux first.'
}
$cfg = "Release R$($RevitVersion.Substring(2))"
$out = Join-Path $PSScriptRoot "publish\$RevitVersion"
$server = Join-Path $PSScriptRoot 'publish\server'
$builds = @($server); if (-not $ServerOnly) { $builds += $out }
foreach ($d in $builds) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
$projects = if ($ServerOnly) { @() } else { 'Plugin', 'Tools' }
foreach ($p in $projects) {
    dotnet publish -c $cfg "$PSScriptRoot\src\RevitFlux.$p\RevitFlux.$p.csproj" -o $out --no-self-contained
    if ($LASTEXITCODE -ne 0) { throw "$p publish failed" }
}
dotnet publish -c Release "$PSScriptRoot\src\RevitFlux.Server\RevitFlux.Server.csproj" -o $server
if ($LASTEXITCODE -ne 0) { throw 'Server publish failed' }

$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$target = Join-Path $addins 'RevitFlux'
$serverTarget = Join-Path $env:USERPROFILE '.revitflux\server'
$targets = @($serverTarget); if (-not $ServerOnly) { $targets += $target }
foreach ($d in $targets) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    New-Item -ItemType Directory -Force $d | Out-Null
}
Copy-Item "$server\*" $serverTarget -Recurse
if ($ServerOnly) { Write-Host "MCP server updated."; return }
Copy-Item "$out\*" $target -Recurse
Copy-Item "$PSScriptRoot\src\RevitFlux.Plugin\RevitFlux.addin" $addins
Write-Host "Installed for Revit $RevitVersion."
Write-Host "Register: claude mcp add revitflux -- `"$serverTarget\RevitFlux.Server.exe`""
