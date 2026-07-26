# start-all.ps1 — Start all development services
param(
    [switch]$AgentOnly,
    [switch]$DesktopOnly,
    [ValidateRange(1, 65534)]
    [int]$PreferredDesktopPort = 4173
)

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Test-LoopbackTcpPortAvailable {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateRange(1, 65534)]
        [int]$Port
    )

    $listener = $null

    try {
        $listener = [System.Net.Sockets.TcpListener]::new(
            [System.Net.IPAddress]::Loopback,
            $Port
        )
        $listener.Start()
        return $true
    }
    catch {
        return $false
    }
    finally {
        if ($null -ne $listener) {
            $listener.Stop()
        }
    }
}

function Get-AvailableDesktopPort {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateRange(1, 65534)]
        [int]$StartPort,

        [ValidateRange(1, 1000)]
        [int]$PortsToTry = 1000
    )

    $lastPort = [Math]::Min($StartPort + $PortsToTry - 1, 65534)

    for ($port = $StartPort; $port -le $lastPort; $port++) {
        if (Test-LoopbackTcpPortAvailable -Port $port) {
            return $port
        }
    }

    throw "No bindable desktop development port was found between $StartPort and $lastPort. The ports may be in use or reserved by Windows."
}

$desktopPort = $null
if (-not $AgentOnly) {
    # Windows can reserve large consecutive port ranges (for example through
    # Hyper-V/WSL). Resolve the desktop port before starting any child process.
    $desktopPort = Get-AvailableDesktopPort -StartPort $PreferredDesktopPort
}

if (-not $DesktopOnly) {
    Write-Host "[1/2] Starting Agent Service (.NET)..." -ForegroundColor Cyan
    Start-Process powershell -ArgumentList "-NoExit", "-Command", `
        "cd '$root\apps\agent-service'; dotnet run" `
        -WindowStyle Normal
    Start-Sleep -Seconds 3
}

if (-not $AgentOnly) {
    $desktopPath = Join-Path $root "apps\desktop"
    $tauriDevUrl = "http://127.0.0.1:$desktopPort"
    $tauriConfig = @{ build = @{ devUrl = $tauriDevUrl } } | ConvertTo-Json -Compress
    $tauriConfigPath = Join-Path `
        ([System.IO.Path]::GetTempPath()) `
        "modern-wingman-tauri-dev-$([System.Guid]::NewGuid().ToString('N')).json"

    # Passing inline JSON through Start-Process -> PowerShell -> pnpm strips its
    # quotes on Windows. A temporary config file avoids that argument parsing.
    [System.IO.File]::WriteAllText(
        $tauriConfigPath,
        $tauriConfig,
        [System.Text.UTF8Encoding]::new($false)
    )

    $escapedDesktopPath = $desktopPath.Replace("'", "''")
    $escapedTauriConfigPath = $tauriConfigPath.Replace("'", "''")
    $desktopCommand = `
        "Set-Location -LiteralPath '$escapedDesktopPath'; " + `
        "try { pnpm tauri dev --config '$escapedTauriConfigPath' } " + `
        "finally { Remove-Item -LiteralPath '$escapedTauriConfigPath' -Force -ErrorAction SilentlyContinue }"

    # The child PowerShell inherits this value, so Vite and Tauri use the same port.
    $previousDesktopPort = $env:WINGMAN_DEV_PORT
    $env:WINGMAN_DEV_PORT = $desktopPort.ToString()

    try {
        Write-Host "[2/2] Starting Desktop (Tauri dev) on $tauriDevUrl..." -ForegroundColor Cyan
        Start-Process powershell -ArgumentList "-NoExit", "-NoProfile", "-Command", $desktopCommand `
            -WindowStyle Normal
    }
    catch {
        Remove-Item -LiteralPath $tauriConfigPath -Force -ErrorAction SilentlyContinue
        throw
    }
    finally {
        if ($null -eq $previousDesktopPort) {
            Remove-Item Env:WINGMAN_DEV_PORT -ErrorAction SilentlyContinue
        }
        else {
            $env:WINGMAN_DEV_PORT = $previousDesktopPort
        }
    }
}

Write-Host "`nAll services started. Close the opened windows to stop." -ForegroundColor Green
