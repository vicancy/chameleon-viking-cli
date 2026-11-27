# SPDX-License-Identifier: GPL-3.0-only
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    dotnet publish VikingTool.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/win-x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw '.NET 10 publish failed' }
    Copy-Item -LiteralPath './artifacts/win-x64/viking.exe' -Destination './viking.exe' -Force
} finally { Pop-Location }
Write-Output "Built: $PSScriptRoot/viking.exe"
