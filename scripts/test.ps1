param([switch]$CoreOnly, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts\smoke' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
Push-Location -LiteralPath $repoRoot
try {
    dotnet run --project .\tests\WhiteboardExporter.Tests\WhiteboardExporter.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    if (-not $CoreOnly) {
        dotnet run --project .\tests\WhiteboardExporter.ImageTests\WhiteboardExporter.ImageTests.csproj -c Release -- (Join-Path $OutputDirectory 'image-writer')
        if ($LASTEXITCODE -ne 0) { throw 'Windows image writer tests failed.' }
        dotnet build .\src\WhiteboardExporter.App\WhiteboardExporter.App.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Desktop test build failed.' }
        $application = Join-Path $repoRoot 'src\WhiteboardExporter.App\bin\Release\net10.0-windows\WhiteboardExporter.exe'
        $process = Start-Process -FilePath $application -ArgumentList @('--smoke-test', ('"{0}"' -f $OutputDirectory)) -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "Desktop smoke test failed; inspect $OutputDirectory." }
        Write-Host "Desktop smoke output: $OutputDirectory"
    }
} finally { Pop-Location }
