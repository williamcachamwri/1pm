# Script khởi chạy nhanh giao diện WinUI 3 cùng Python Backend gốc (handset.server)
$ErrorActionPreference = "Stop"
Write-Host "=== Khởi chạy 1pm (WinUI 3 Floating Edition) ===" -ForegroundColor Cyan
dotnet run --project "$PSScriptRoot\1pm.csproj" -c Release
