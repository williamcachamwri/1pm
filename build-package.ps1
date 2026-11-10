param(
    [string]$Version = "latest"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

Write-Host "=== Building 1pm Release Bundle (Version: $Version) ===" -ForegroundColor Cyan

# 1. Clean previous dist
$dist = Join-Path $root "dist"
if (Test-Path $dist) {
    Remove-Item -Recurse -Force $dist
}
$bundle = Join-Path $dist "bundle"
$bundleBin = Join-Path $bundle "bin"
New-Item -ItemType Directory -Force -Path $bundleBin | Out-Null

# 2. Publish WinUI 3 App into bundle/bin
Write-Host "Publishing self-contained WinUI 3 application..." -ForegroundColor Yellow
dotnet restore (Join-Path $root "1pm.csproj")
dotnet publish (Join-Path $root "1pm.csproj") -c Release -r win-x64 --self-contained true -o $bundleBin

# 3. Copy python engine, codecs, docs, and scripts
Write-Host "Copying backend engine and assets..." -ForegroundColor Yellow
Copy-Item -Recurse -Force (Join-Path $root "iphone-mirror-engine") (Join-Path $bundle "iphone-mirror-engine")
Copy-Item (Join-Path $root "README.md") $bundle -Force
Copy-Item (Join-Path $root "run-winui.ps1") $bundle -Force
if (Test-Path (Join-Path $root "docs")) {
    Copy-Item -Recurse -Force (Join-Path $root "docs") (Join-Path $bundle "docs")
}

# 4. Verify libfdk-aac-2.dll
$fdkPath = Join-Path $bundle "iphone-mirror-engine\handset\libfdk-aac-2.dll"
if (-not (Test-Path $fdkPath)) {
    throw "libfdk-aac-2.dll is missing from $fdkPath"
}

# 5. Compile Root Launcher (1pm.exe) for clean portable folder
Write-Host "Compiling portable folder launcher..." -ForegroundColor Yellow
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$iconPath = Join-Path $root "iphone-mirror-engine\packaging\pane.ico"
$launcherCs = Join-Path $root "packaging\Launcher.cs"
$portableLauncher = Join-Path $bundle "1pm.exe"
$launcherIconArg = "/win32icon:$iconPath"
$launcherOutArg = "/out:$portableLauncher"
& $csc /target:winexe /optimize+ $launcherIconArg $launcherOutArg $launcherCs

# 6. Compress clean portable bundle into zip
Write-Host "Compressing portable zip package..." -ForegroundColor Yellow
$zipName = Join-Path $root "1pm-$Version-portable-windows-x64.zip"
if (Test-Path $zipName) { Remove-Item -Force $zipName }
Compress-Archive -Path "$bundle\*" -DestinationPath $zipName -Force
Write-Host "Created portable archive: $zipName" -ForegroundColor Green

# 7. Compress bundle for single-file embedded payload
Write-Host "Creating embedded payload archive..." -ForegroundColor Yellow
$payloadZip = Join-Path $dist "payload.zip"
Compress-Archive -Path "$bundle\*" -DestinationPath $payloadZip -CompressionLevel Optimal

if (-not (Test-Path $payloadZip)) {
    throw "Payload archive was not generated at $payloadZip"
}

# 8. Compile 100% Single-File Standalone Executable 1pm.exe
Write-Host "Compiling Standalone Single-File 1pm.exe..." -ForegroundColor Yellow
$singleCs = Join-Path $root "packaging\SingleFileApp.cs"
$standaloneExe = Join-Path $root "1pm.exe"
if (Test-Path $standaloneExe) { Remove-Item -Force $standaloneExe }

$resourceArg = "/resource:$($payloadZip),payload.zip"
$standaloneIconArg = "/win32icon:$iconPath"
$standaloneOutArg = "/out:$standaloneExe"

Write-Host "CSC Arguments: $resourceArg $standaloneIconArg $standaloneOutArg"
& $csc /target:winexe /optimize+ /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll $resourceArg $standaloneIconArg $standaloneOutArg $singleCs

if (-not (Test-Path $standaloneExe)) {
    throw "Failed to compile standalone binary at $standaloneExe"
}

$finalSize = (Get-Item $standaloneExe).Length
Write-Host "SUCCESS: Created Standalone 1pm.exe ($([math]::Round($finalSize / 1MB, 2)) MB)" -ForegroundColor Green
