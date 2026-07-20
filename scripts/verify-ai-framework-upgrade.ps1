[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [switch]$RequireExternalAcceptance
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$service = Join-Path $root 'apps\agent-service'
$project = Join-Path $service 'AgentService.csproj'
$tests = Join-Path $service 'tests\UnitTests\AgentService.UnitTests.csproj'
$publishDirectory = Join-Path $service 'publish-verification\agent-service-win-x64'

function Assert-ResolvedPackage {
    param(
        [Parameter(Mandatory)] [object]$Target,
        [Parameter(Mandatory)] [string]$Package
    )

    $property = $Target.PSObject.Properties[$Package]
    if ($null -eq $property) {
        throw "Expected resolved package '$Package' was not found in project.assets.json."
    }
}

function Invoke-Dotnet {
    param([Parameter(Mandatory)] [string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Assert-EnvironmentValue {
    param([Parameter(Mandatory)] [string]$Name)

    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($Name))) {
        throw "External acceptance requires environment variable $Name."
    }
}

Push-Location $service
try {
    Invoke-Dotnet @('restore', $project, '--locked-mode')

    $assets = Get-Content (Join-Path $service 'obj\project.assets.json') -Raw | ConvertFrom-Json
    $target = $assets.targets.'net10.0'
    Assert-ResolvedPackage -Target $target -Package 'Microsoft.Agents.AI/1.13.0'
    Assert-ResolvedPackage -Target $target -Package 'Microsoft.Agents.AI.Abstractions/1.13.0'
    Assert-ResolvedPackage -Target $target -Package 'Microsoft.Agents.AI.OpenAI/1.13.0'
    Assert-ResolvedPackage -Target $target -Package 'Microsoft.Agents.AI.Workflows/1.13.0'
    Assert-ResolvedPackage -Target $target -Package 'Microsoft.Agents.AI.GitHub.Copilot/1.13.0-rc1'
    Assert-ResolvedPackage -Target $target -Package 'GitHub.Copilot.SDK/1.0.0'
    Assert-ResolvedPackage -Target $target -Package 'Microsoft.Extensions.AI/10.8.0'
    Assert-ResolvedPackage -Target $target -Package 'Microsoft.Extensions.AI.Abstractions/10.8.0'
    Assert-ResolvedPackage -Target $target -Package 'Microsoft.Extensions.AI.Evaluation/10.8.0'
    Assert-ResolvedPackage -Target $target -Package 'Microsoft.Extensions.AI.OpenAI/10.8.0'
    Assert-ResolvedPackage -Target $target -Package 'OpenAI/2.12.0'
    Assert-ResolvedPackage -Target $target -Package 'System.ClientModel/1.14.0'

    Invoke-Dotnet @('build', $project, '--configuration', 'Release', '--no-restore')
    Invoke-Dotnet @('restore', $tests, '--locked-mode')
    Invoke-Dotnet @('test', $tests, '--configuration', 'Release', '--no-restore')

    if ($RequireExternalAcceptance) {
        Assert-EnvironmentValue 'WINGMAN_TEST_NEO4J_URI'
        Assert-EnvironmentValue 'WINGMAN_BENCHMARK_NEO4J_URI'
        Assert-EnvironmentValue 'WINGMAN_BENCHMARK_NOPCOMMERCE_ROOT'
        if (-not (Test-Path ([Environment]::GetEnvironmentVariable('WINGMAN_BENCHMARK_NOPCOMMERCE_ROOT')) -PathType Container)) {
            throw 'WINGMAN_BENCHMARK_NOPCOMMERCE_ROOT does not point to an existing directory.'
        }

    }

    if (-not $SkipPublish) {
        Invoke-Dotnet @('restore', $project, '--runtime', 'win-x64', '--locked-mode')
        Invoke-Dotnet @('publish', $project, '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'false', '--no-restore', '--output', $publishDirectory)
        $copilotBinary = Join-Path $publishDirectory 'runtimes\win-x64\native\copilot.exe'
        if (-not (Test-Path $copilotBinary -PathType Leaf)) {
            throw "The bundled Copilot CLI was not published: $copilotBinary"
        }
        & $copilotBinary --version
        if ($LASTEXITCODE -ne 0) {
            throw "The published bundled Copilot CLI could not report its version (exit code $LASTEXITCODE)."
        }
    }
}
finally {
    Pop-Location
}
