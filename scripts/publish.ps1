param([ValidateSet('win-arm64', 'win-x64')][string[]]$Runtimes = @('win-arm64', 'win-x64'), [switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$distributionRoot = Join-Path $repoRoot 'artifacts\releases'
New-Item -ItemType Directory -Path $distributionRoot -Force | Out-Null
Push-Location -LiteralPath $repoRoot
try {
    foreach ($runtime in $Runtimes) {
        $destination = Join-Path $repoRoot "artifacts\publish\$runtime"
        $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
        dotnet publish .\src\WhiteboardExporter.App\WhiteboardExporter.App.csproj -c Release -r $runtime --self-contained $selfContained -p:PublishSingleFile=false -o $destination
        if ($LASTEXITCODE -ne 0) { throw "Publishing $runtime failed." }
        Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md'), (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination $destination -Force
        foreach ($licenseName in @('LICENSE', 'LICENSE.md', 'LICENSE.txt')) {
            $licensePath = Join-Path $repoRoot $licenseName
            if (Test-Path -LiteralPath $licensePath -PathType Leaf) { Copy-Item -LiteralPath $licensePath -Destination $destination -Force }
        }
        $documentation = Join-Path $destination 'docs'
        New-Item -ItemType Directory -Path $documentation -Force | Out-Null
        # Older packages included local development history. Remove that managed output.
        $legacyHistory = [IO.Path]::GetFullPath((Join-Path $documentation 'history'))
        $publishPrefix = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\publish')).TrimEnd('\') + '\'
        if (-not $legacyHistory.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Documentation path escaped publish directory.' }
        if (Test-Path -LiteralPath $legacyHistory) {
            $pathToCheck = Get-Item -LiteralPath $legacyHistory
            while ($pathToCheck -and $pathToCheck.FullName -ne $repoRoot) {
                if ($pathToCheck.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to clean linked publish documentation.' }
                $pathToCheck = $pathToCheck.Parent
            }
            if (Get-ChildItem -LiteralPath $legacyHistory -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Refusing to clean linked history entries.' }
            Remove-Item -LiteralPath $legacyHistory -Recurse -Force
        }
        foreach ($document in @('ACCEPTANCE.md', 'SCENE-FORMAT.md', 'minimal-redraw.html')) {
            Copy-Item -LiteralPath (Join-Path $repoRoot ('docs\' + $document)) -Destination $documentation -Force
        }
        $licenses = Join-Path $documentation 'licenses'
        New-Item -ItemType Directory -Path $licenses -Force | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs\licenses') -File | Copy-Item -Destination $licenses -Force
        $archive = Join-Path $distributionRoot "WhiteboardExporter-$runtime.zip"
        Compress-Archive -Path (Join-Path $destination '*') -DestinationPath $archive -Force
        Write-Host "Published: $archive"
    }
    $checksums = Get-ChildItem -LiteralPath $distributionRoot -Filter 'WhiteboardExporter-*.zip' -File | Sort-Object Name | ForEach-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name
    }
    Set-Content -LiteralPath (Join-Path $distributionRoot 'SHA256SUMS.txt') -Value $checksums -Encoding ascii
} finally { Pop-Location }
