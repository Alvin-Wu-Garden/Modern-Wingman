[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path $_ -PathType Container })]
    [string]$SourceRoot,
    [Parameter(Mandatory)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$templateRoot = Join-Path $repoRoot 'templates\wingman-jira-mcp'
$packageJson = Join-Path $SourceRoot 'package.json'
if (-not (Test-Path $packageJson -PathType Leaf)) { throw "SourceRoot must contain package.json." }

$npm = Join-Path $repoRoot 'apps\agent-service\tools\runtimes\node\24.18.0\npm.cmd'
if (-not (Test-Path $npm -PathType Leaf)) { $npm = 'npm.cmd' }

$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("wingman-jira-package-" + [guid]::NewGuid().ToString('N'))
$workSource = Join-Path $workRoot 'source'
$originalLocation = Get-Location
try {
    New-Item -ItemType Directory -Path $workRoot | Out-Null
    Copy-Item -LiteralPath $SourceRoot -Destination $workSource -Recurse
    Push-Location $workSource
    & $npm ci --ignore-scripts
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE." }
    & $npm run build
    if ($LASTEXITCODE -ne 0) { throw "npm run build failed with exit code $LASTEXITCODE." }
    & $npm prune --omit=dev --ignore-scripts
    if ($LASTEXITCODE -ne 0) { throw "npm prune failed with exit code $LASTEXITCODE." }
    Pop-Location

    $entrypoint = Join-Path $workSource 'build\index.js'
    $modules = Join-Path $workSource 'node_modules'
    if (-not (Test-Path $entrypoint -PathType Leaf) -or -not (Test-Path $modules -PathType Container)) { throw "The Jira source build did not produce build/index.js and production node_modules." }
    if (Test-Path $OutputPath) { throw "OutputPath already exists: $OutputPath" }

    Copy-Item -LiteralPath $templateRoot -Destination $OutputPath -Recurse
    $vendor = Join-Path $OutputPath 'vendor\mcp-jira-server'
    New-Item -ItemType Directory -Path $vendor -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $workSource 'build') -Destination (Join-Path $vendor 'build') -Recurse
    Copy-Item -LiteralPath $modules -Destination (Join-Path $vendor 'node_modules') -Recurse
    foreach ($file in @('package.json', 'package-lock.json', 'LICENSE')) {
        $source = Join-Path $workSource $file
        if (Test-Path $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination (Join-Path $vendor $file) }
    }

    $upstream = Get-Content -LiteralPath (Join-Path $workSource 'package.json') -Raw | ConvertFrom-Json
    $manifestPath = Join-Path $OutputPath '.codex-plugin\plugin.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $manifest.version = $upstream.version
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    Write-Host "Created runnable Wingman Jira Plugin: $OutputPath"
}
finally {
    Set-Location -LiteralPath $originalLocation
    if (Test-Path $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force }
}
