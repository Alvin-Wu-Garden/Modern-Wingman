[CmdletBinding()]
param([switch]$Force, [switch]$KeepDownloads)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $root 'runtime-manifest.json') -Raw | ConvertFrom-Json
$downloads = Join-Path $root 'downloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null

foreach ($runtime in $manifest.runtimes) {
    $destination = Join-Path $root ($runtime.destination -replace '/', [IO.Path]::DirectorySeparatorChar)
    $executable = Join-Path $root ($runtime.executable -replace '/', [IO.Path]::DirectorySeparatorChar)
    if ((Test-Path -LiteralPath $executable) -and -not $Force) {
        Write-Host "$($runtime.id) $($runtime.version) is already available."
        continue
    }

    $archive = Join-Path $downloads $runtime.archive
    if (-not (Test-Path -LiteralPath $archive)) {
        Write-Host "Downloading $($runtime.id) $($runtime.version)..."
        Invoke-WebRequest -Uri $runtime.url -OutFile $archive
    }
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $runtime.sha256.ToLowerInvariant()) {
        throw "Checksum mismatch for $($runtime.archive). Expected $($runtime.sha256), got $actual."
    }

    $staging = Join-Path $downloads ("extract-" + $runtime.id)
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    Expand-Archive -LiteralPath $archive -DestinationPath $staging -Force
    $source = Join-Path $staging ($runtime.archiveRoot -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $source)) { throw "Archive root is missing: $source" }

    $resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedDestination = [IO.Path]::GetFullPath($destination)
    if (-not $resolvedDestination.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace a destination outside $root."
    }
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Move-Item -LiteralPath $source -Destination $destination
    Remove-Item -LiteralPath $staging -Recurse -Force
    if (-not (Test-Path -LiteralPath $executable)) { throw "Expected executable is missing: $executable" }
    Write-Host "Installed $($runtime.id) $($runtime.version)."
}

if (-not $KeepDownloads -and (Test-Path -LiteralPath $downloads)) {
    Remove-Item -LiteralPath $downloads -Recurse -Force
}
