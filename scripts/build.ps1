param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repoRoot
try {
    dotnet restore .\WhiteboardExporter.slnx
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
    dotnet build .\WhiteboardExporter.slnx -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
} finally { Pop-Location }
