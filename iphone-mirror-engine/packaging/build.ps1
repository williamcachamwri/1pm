# Builds dist\Pane.exe from the repository root.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

python -m PyInstaller --noconfirm --clean `
  --distpath "$root\dist" `
  --workpath "$root\build" `
  "$root\packaging\pane.spec"

Write-Host "Built $root\dist\Pane.exe"
