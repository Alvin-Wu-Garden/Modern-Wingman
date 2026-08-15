# start-all.ps1 — VS Code 全端 Debug 的唯一啟動前置作業。
#
# AgentService 與 Tauri 必須由 VS Code Debugger 直接啟動，才能正確掛載中斷點，
# 並在使用者按下 Stop 時結束。本腳本只負責：
# 1. 清除上次異常中斷留下的 Modern Wingman 程序。
# 2. 確認固定開發連接埠沒有被其他應用程式占用。
# 3. 建置 AgentService 與 Tauri Debug 執行檔。
# 4. 啟動 Vite，並保存可安全驗證的程序 ownership。
[CmdletBinding()]
param(
    # 驗證腳本可略過耗時建置；正常 VS Code Debug 不應使用此參數。
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$workspaceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$desktopRoot = Join-Path $workspaceRoot "apps\desktop"
$agentProject = Join-Path $workspaceRoot "apps\agent-service\AgentService.csproj"
$tauriRoot = Join-Path $desktopRoot "src-tauri"
$stopScript = Join-Path $PSScriptRoot "stop-all.ps1"
$runtimeStatePath = Join-Path $env:TEMP "modern-wingman-debug-runtime.json"
$viteOutputPath = Join-Path $env:TEMP "modern-wingman-vite.stdout.log"
$viteErrorPath = Join-Path $env:TEMP "modern-wingman-vite.stderr.log"
# 目前 Debug 流程的唯一必要 Listener：Vite、Agent REST 與 Neo4j Bolt。
# Neo4j Browser 的 HTTP port 由 Neo4j 自身設定管理，不應在啟動腳本重複維護。
$requiredPorts = @(4173, 5002, 17688)

function Get-ListeningProcessIds {
    <# 取得指定 TCP port 的唯一監聽程序；沒有 listener 時回傳空陣列。 #>
    param([Parameter(Mandatory)][int]$Port)

    return @(
        Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty OwningProcess -Unique
    )
}

function Get-ProcessDescription {
    <# 產生不包含機密資料的程序說明，讓使用者可以直接定位 port 衝突。 #>
    param([Parameter(Mandatory)][int]$ProcessId)

    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        return "PID $ProcessId（程序已結束）"
    }

    return "PID $ProcessId，$($process.Name)，$($process.ExecutablePath)"
}

function Assert-RequiredPortsAreFree {
    <# Debug 一律使用固定 port；禁止靜默換號，避免前後端連到不同 instance。 #>
    $conflicts = foreach ($port in $requiredPorts) {
        foreach ($processId in Get-ListeningProcessIds -Port $port) {
            "127.0.0.1:$port → $(Get-ProcessDescription -ProcessId $processId)"
        }
    }

    if (@($conflicts).Count -gt 0) {
        throw "Modern Wingman Debug 無法啟動，固定連接埠仍被占用：`n$($conflicts -join "`n")"
    }
}

function Invoke-CheckedCommand {
    <# 執行建置命令並保留原始輸出；非零結束碼立即阻止 Debug 啟動。 #>
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string]$DisplayName
    )

    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$DisplayName 失敗，結束碼：$LASTEXITCODE。"
        }
    }
    finally {
        Pop-Location
    }
}

function Get-ProcessStartTimeUtcTicks {
    <# PID 可能被 Windows 重複使用，因此 ownership 同時保存程序開始時間。 #>
    param([Parameter(Mandatory)][int]$ProcessId)

    return (Get-Process -Id $ProcessId -ErrorAction Stop).StartTime.ToUniversalTime().Ticks
}

function Save-RuntimeState {
    <# 原子寫入 runtime state，避免 VS Code 中斷腳本時留下半份 JSON。 #>
    param([Parameter(Mandatory)][object]$State)

    $temporaryPath = "$runtimeStatePath.tmp"
    $State | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temporaryPath -Encoding utf8
    Move-Item -LiteralPath $temporaryPath -Destination $runtimeStatePath -Force
}

function Wait-ForViteReady {
    <# 等待 Vite 真正提供本專案入口；允許 Vite 在 URL 後加入快取時間戳。 #>
    param([ValidateRange(1, 120)][int]$TimeoutSeconds = 45)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 300
        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:4173/" -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200 -and $response.Content -match '/src/main\.tsx(?:\?[^"'']*)?') {
                return
            }
        }
        catch {
            # Vite 啟動期間短暫拒絕連線屬正常狀況，直到 timeout 才回報失敗。
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    $diagnostic = if (Test-Path -LiteralPath $viteErrorPath) {
        (Get-Content -LiteralPath $viteErrorPath -Tail 20 -ErrorAction SilentlyContinue) -join "`n"
    }
    else {
        "Vite 沒有產生錯誤紀錄。"
    }
    throw "Vite 在 $TimeoutSeconds 秒內未就緒。`n$diagnostic"
}

Write-Host "[1/4] 清理上次 Debug 遺留的 Modern Wingman 程序..." -ForegroundColor Cyan
& powershell -NoProfile -ExecutionPolicy Bypass -File $stopScript -ForRestart -Quiet
if ($LASTEXITCODE -ne 0) {
    throw "無法完成啟動前清理，請查看上方 stop-all 診斷。"
}
Assert-RequiredPortsAreFree

if (-not $SkipBuild) {
    Write-Host "[2/4] 建置 AgentService..." -ForegroundColor Cyan
    Invoke-CheckedCommand `
        -FilePath "dotnet" `
        -Arguments @("build", $agentProject, "/property:GenerateFullPaths=true", "/consoleloggerparameters:NoSummary") `
        -WorkingDirectory $workspaceRoot `
        -DisplayName "AgentService 建置"

    Write-Host "[3/4] 建置 Tauri Debug 執行檔..." -ForegroundColor Cyan
    Invoke-CheckedCommand `
        -FilePath "cargo" `
        -Arguments @("build") `
        -WorkingDirectory $tauriRoot `
        -DisplayName "Tauri 建置"
}
else {
    Write-Host "[2/4] 已略過 AgentService 建置（驗證模式）。" -ForegroundColor DarkGray
    Write-Host "[3/4] 已略過 Tauri 建置（驗證模式）。" -ForegroundColor DarkGray
}

Write-Host "[4/4] 啟動 Vite：http://127.0.0.1:4173..." -ForegroundColor Cyan
Remove-Item -LiteralPath $viteOutputPath, $viteErrorPath -Force -ErrorAction SilentlyContinue
$pnpmCommand = (Get-Command "pnpm.cmd" -ErrorAction Stop).Source
$viteLauncher = $null

try {
    $viteLauncher = Start-Process `
        -FilePath $pnpmCommand `
        -ArgumentList @("dev") `
        -WorkingDirectory $desktopRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput $viteOutputPath `
        -RedirectStandardError $viteErrorPath `
        -PassThru

    $state = [ordered]@{
        SchemaVersion = 1
        WorkspaceRoot = $workspaceRoot
        CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        ViteLauncherProcessId = $viteLauncher.Id
        ViteLauncherStartTimeUtcTicks = Get-ProcessStartTimeUtcTicks -ProcessId $viteLauncher.Id
        ViteListenerProcessId = $null
        ViteListenerStartTimeUtcTicks = $null
        ViteOutputPath = $viteOutputPath
        ViteErrorPath = $viteErrorPath
    }
    Save-RuntimeState -State $state

    Wait-ForViteReady
    $listenerIds = @(Get-ListeningProcessIds -Port 4173)
    if ($listenerIds.Count -ne 1) {
        throw "Vite 已回應，但 4173 的 listener 數量不是 1：$($listenerIds -join ', ')。"
    }

    $state.ViteListenerProcessId = $listenerIds[0]
    $state.ViteListenerStartTimeUtcTicks = Get-ProcessStartTimeUtcTicks -ProcessId $listenerIds[0]
    Save-RuntimeState -State $state
}
catch {
    if ($null -ne $viteLauncher -and -not $viteLauncher.HasExited) {
        & taskkill.exe /PID $viteLauncher.Id /T /F 2>$null | Out-Null
    }
    Remove-Item -LiteralPath $runtimeStatePath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $viteOutputPath, $viteErrorPath -Force -ErrorAction SilentlyContinue
    throw
}

Write-Host "Modern Wingman Debug 相依服務已就緒；VS Code 接著會啟動 AgentService 與 Tauri。" -ForegroundColor Green
