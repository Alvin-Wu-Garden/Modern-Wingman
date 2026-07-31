# start-all.ps1 — 啟動 Modern Wingman 的本機開發服務
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

    throw "在 $StartPort 到 $lastPort 之間找不到可綁定的桌面開發連接埠；這些連接埠可能已被占用或由 Windows 保留。"
}

$desktopPort = $null
if (-not $AgentOnly) {
    # Windows 可能由 Hyper-V／WSL 保留一整段連續連接埠。
    # 必須先找到可實際 bind 的連接埠，再啟動任何子程序，避免只檢查 netstat 的誤判。
    $desktopPort = Get-AvailableDesktopPort -StartPort $PreferredDesktopPort
}

if (-not $DesktopOnly) {
    Write-Host "[1/2] 正在啟動 Agent Service (.NET)..." -ForegroundColor Cyan
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

    # Windows 會在 Start-Process → PowerShell → pnpm 的多層參數傳遞中移除 inline JSON 引號。
    # 使用 UTF-8 暫存設定檔可避免 Tauri 取得損壞的 JSON。
    [System.IO.File]::WriteAllText(
        $tauriConfigPath,
        $tauriConfig,
        [System.Text.UTF8Encoding]::new($false)
    )

    $escapedDesktopPath = $desktopPath.Replace("'", "''")
    $escapedTauriConfigPath = $tauriConfigPath.Replace("'", "''")
    $desktopCommand = `
        "Set-Location -LiteralPath '$escapedDesktopPath'; " + `
        "try { pnpm.cmd tauri dev --config '$escapedTauriConfigPath' } " + `
        "finally { Remove-Item -LiteralPath '$escapedTauriConfigPath' -Force -ErrorAction SilentlyContinue }"

    # 子 PowerShell 會繼承此環境變數，確保 Vite 與 Tauri 使用同一個動態連接埠。
    $previousDesktopPort = $env:WINGMAN_DEV_PORT
    $env:WINGMAN_DEV_PORT = $desktopPort.ToString()

    try {
        Write-Host "[2/2] 正在啟動 Desktop (Tauri dev)：$tauriDevUrl..." -ForegroundColor Cyan
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

Write-Host "`n所有服務已啟動；關閉開啟的視窗即可停止。" -ForegroundColor Green
