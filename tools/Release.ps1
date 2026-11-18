# Releases a version: sets it in the csproj, commits everything, tags vX.Y.Z and pushes.
# The GitHub Actions workflow then builds and publishes to Gompex/DalamudPlugins.
# Usage:  & C:\Dev\PlaterXIV\tools\Release.ps1 1.0.0
param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3" }
if ((git tag --list "v$Version")) { throw "Tag v$Version already exists" }

$csproj = 'PlaterXIV\PlaterXIV.csproj'
$text = Get-Content $csproj -Raw
$text = $text -replace '<Version>[^<]+</Version>', "<Version>$Version.0</Version>"
Set-Content $csproj $text -NoNewline

git add -A
git commit -m "Release v$Version" | Out-Null
git tag "v$Version"
git push origin main
git push origin "v$Version"

Write-Host "Pushed v$Version. Follow the build with:  gh run watch --repo Gompex/PlaterXIV" -ForegroundColor Green
