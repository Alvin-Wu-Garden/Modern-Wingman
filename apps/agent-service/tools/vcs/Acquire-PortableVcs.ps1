[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$KeepDownloads
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$manifestPath = Join-Path $root 'runtime-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$downloads = Join-Path $root 'downloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null

foreach ($runtime in $manifest.runtimes) {
    $destination = Join-Path $root $runtime.destination
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

    $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $runtime.sha256.ToLowerInvariant()) {
        throw "Checksum mismatch for $($runtime.archive). Expected $($runtime.sha256), got $actualHash."
    }

    $resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedDestination = [IO.Path]::GetFullPath($destination)
    if (-not $resolvedDestination.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace a destination outside $root."
    }

    if (Test-Path -LiteralPath $destination) {
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force

    if (-not (Test-Path -LiteralPath $executable)) {
        throw "Archive did not contain the expected executable: $executable"
    }
    Write-Host "Installed $($runtime.id) $($runtime.version)."
}

if (-not $KeepDownloads -and (Test-Path -LiteralPath $downloads)) {
    Remove-Item -LiteralPath $downloads -Recurse -Force
}
