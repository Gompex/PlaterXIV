# One-time setup: private source repo Gompex/PlaterXIV + the RELEASE_TOKEN secret for the release workflow.
# Run from anywhere:  & C:\Dev\PlaterXIV\tools\Setup-GitHub.ps1
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI missing. Install: & `"$env:LOCALAPPDATA\Microsoft\WindowsApps\winget.exe`" install --id GitHub.cli -e"
}
$ErrorActionPreference = 'Continue'
gh auth status *> $null
$loggedIn = $LASTEXITCODE -eq 0
$ErrorActionPreference = 'Stop'
if (-not $loggedIn) { gh auth login }

if (-not (Test-Path .git)) { git init | Out-Null }
git add -A
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) { git commit -m "Plater for FF14" | Out-Null }
git branch -M main

$hasOrigin = (git remote) -contains 'origin'
if (-not $hasOrigin) {
    $ErrorActionPreference = 'Continue'   # "repo not found" must not stop the script
    gh repo view Gompex/PlaterXIV *> $null
    $exists = $LASTEXITCODE -eq 0
    $ErrorActionPreference = 'Stop'
    if ($exists) {
        git remote add origin https://github.com/Gompex/PlaterXIV.git
        git push -u origin main
    }
    else {
        gh repo create Gompex/PlaterXIV --private --source . --remote origin --push
    }
}
else {
    git push -u origin main
}

Write-Host ""
Write-Host "Now paste the RELEASE_TOKEN (the same kind of token as for InventoryTriage:" -ForegroundColor Cyan
Write-Host "fine-grained, repository Gompex/DalamudPlugins, Contents: Read and write)." -ForegroundColor Cyan
gh secret set RELEASE_TOKEN --repo Gompex/PlaterXIV

Write-Host "Done. Release with:  & C:\Dev\PlaterXIV\tools\Release.ps1 1.0.0" -ForegroundColor Green
