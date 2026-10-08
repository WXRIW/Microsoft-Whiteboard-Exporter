param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repoRoot
try {
    $runArguments = @('run', '--project', '.\src\WhiteboardExporter.App\WhiteboardExporter.App.csproj', '-c', $Configuration)
    if ($NoBuild) { $runArguments += '--no-build' }
    dotnet @runArguments
    if ($LASTEXITCODE -ne 0) { throw 'The application did not start successfully.' }
} finally { Pop-Location }
