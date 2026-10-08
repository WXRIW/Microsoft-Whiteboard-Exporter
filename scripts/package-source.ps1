$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$releaseRoot = Join-Path $repoRoot 'artifacts\releases'
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$archivePath = Join-Path $releaseRoot 'WhiteboardExporter-source.zip'
$temporaryPath = Join-Path $releaseRoot ('source-' + [Guid]::NewGuid().ToString('N') + '.tmp')
$rootFiles = @('README.md', 'THIRD-PARTY-NOTICES.md', 'WhiteboardExporter.slnx', '.gitignore', '.gitattributes', '.editorconfig', 'global.json')
foreach ($licenseName in @('LICENSE', 'LICENSE.md', 'LICENSE.txt')) {
    if (Test-Path -LiteralPath (Join-Path $repoRoot $licenseName) -PathType Leaf) { $rootFiles += $licenseName }
}
$files = @($rootFiles | ForEach-Object { Get-Item -LiteralPath (Join-Path $repoRoot $_) })
foreach ($directory in @('src', 'tests', 'samples', 'scripts')) {
    $files += Get-ChildItem -LiteralPath (Join-Path $repoRoot $directory) -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj|node_modules|__pycache__|\.venv|venv|\.vs|\.idea|\.git|artifacts|profiles|Browser|EBWebView|private-regression|private-fixtures|TestResults|coverage)[\\/]' -and
        $_.Name -notmatch '\.(user|suo|rsuser|userosscache|docstates|userprefs|wbarchive|whiteboard|zip|tmp|temp|partial|bak|log|binlog|dmp|trx|coverage|coveragexml|py[cod]|pfx|p12)$' -and
        $_.Name -notmatch '(\.previous-|\.DotSettings\.user$|~$)' -and
        $_.Name -notin @('account.json', 'export-state.json', 'Thumbs.db', 'Desktop.ini', '.DS_Store') -and
        ($_.Name -notmatch '^\.env($|\.)' -or $_.Name -eq '.env.example')
    }
}
# Include maintained project documentation, never local development history.
foreach ($document in @('ACCEPTANCE.md', 'SCENE-FORMAT.md', 'minimal-redraw.html')) {
    $files += Get-Item -LiteralPath (Join-Path $repoRoot ('docs\' + $document))
}
$files += Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs\licenses') -File
try {
    $stream = $null
    $archive = $null
    try {
        $stream = [IO.File]::Create($temporaryPath)
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
        foreach ($file in $files | Sort-Object FullName -Unique) {
            $prefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            if (-not $file.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source path escaped workspace.' }
            # GetRelativePath is unavailable in Windows PowerShell 5.1/.NET Framework.
            $relative = $file.FullName.Substring($prefix.Length)
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative.Replace('\', '/')) | Out-Null
        }
    } finally {
        if ($archive) { $archive.Dispose() }
        if ($stream) { $stream.Dispose() }
    }
    Move-Item -LiteralPath $temporaryPath -Destination $archivePath -Force
    $checksums = Get-ChildItem -LiteralPath $releaseRoot -Filter 'WhiteboardExporter-*.zip' -File | Sort-Object Name | ForEach-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name
    }
    Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Value $checksums -Encoding ascii
    Write-Host "Packaged source: $archivePath"
} finally { if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force } }
