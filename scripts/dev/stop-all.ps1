# stop-all.ps1 — 關閉 Modern Wingman 全端 Debug 的所有本機程序。
#
# 本腳本只終止能以「ownership + 程序開始時間」或「固定執行檔路徑」驗證為
# Modern Wingman 的程序。即使其他應用程式占用相同 port，也絕不直接誤殺。
[CmdletBinding()]
param(
    # 啟動前清理使用；語意與一般停止相同，但允許呼叫端表達用途。
    [switch]$ForRestart,

    # 隱藏一般完成訊息；錯誤與無法釋放的 port 仍會顯示。
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"
$workspaceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$desktopRoot = Join-Path $workspaceRoot "apps\desktop"
$agentExecutable = [System.IO.Path]::GetFullPath(
    (Join-Path $workspaceRoot "apps\agent-service\bin\Debug\net10.0\AgentService.exe"))
$tauriExecutable = [System.IO.Path]::GetFullPath(
    "D:\CargoTarget\modern-wingman\debug\modern-wingman-desktop.exe")
$neo4jRuntimeRoot = [System.IO.Path]::GetFullPath(
    (Join-Path ([Environment]::GetFolderPath("UserProfile")) ".Wingman\neo4j"))
$neo4jOwnershipPath = Join-Path $neo4jRuntimeRoot "managed-v3-owner.json"
$runtimeStatePath = Join-Path $env:TEMP "modern-wingman-debug-runtime.json"
$viteOutputPath = Join-Path $env:TEMP "modern-wingman-vite.stdout.log"
$viteErrorPath = Join-Path $env:TEMP "modern-wingman-vite.stderr.log"
$portsToVerify = @(4173, 5002, 17475, 17688)
$stoppedProcessIds = [System.Collections.Generic.HashSet[int]]::new()

function Get-ListeningProcessIds {
    <# 取得指定 TCP port 的唯一監聽程序；沒有 listener 時回傳空陣列。 #>
    param([Parameter(Mandatory)][int]$Port)

    return @(
        Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty OwningProcess -Unique
    )
}

function Get-ProcessSnapshot {
    <# 同時取得 Win32 command line 與 .NET StartTime，供安全身分驗證。 #>
    param([Parameter(Mandatory)][int]$ProcessId)

    $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction SilentlyContinue
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $cim -or $null -eq $process) {
        return $null
    }

    return [pscustomobject]@{
        ProcessId = $ProcessId
        Name = [string]$cim.Name
        ExecutablePath = [string]$cim.ExecutablePath
        CommandLine = [string]$cim.CommandLine
        ParentProcessId = [int]$cim.ParentProcessId
        StartTimeUtcTicks = $process.StartTime.ToUniversalTime().Ticks
    }
}

function Test-SamePath {
    <# Windows 路徑不分大小寫；不存在的檔案也要能用字串安全比較。 #>
    param([string]$Left, [string]$Right)

    if ([string]::IsNullOrWhiteSpace($Left) -or [string]::IsNullOrWhiteSpace($Right)) {
        return $false
    }
    return [string]::Equals(
        [System.IO.Path]::GetFullPath($Left),
        [System.IO.Path]::GetFullPath($Right),
        [StringComparison]::OrdinalIgnoreCase)
}

function Stop-VerifiedProcessTree {
    <# 驗證完成後才用 taskkill /T 回收整棵子程序，避免殘留 pnpm 或 Java launcher。 #>
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][scriptblock]$Validator,
        [Parameter(Mandatory)][string]$DisplayName
    )

    if ($stoppedProcessIds.Contains($ProcessId)) {
        return
    }
    $snapshot = Get-ProcessSnapshot -ProcessId $ProcessId
    if ($null -eq $snapshot) {
        return
    }
    if (-not (& $Validator $snapshot)) {
        Write-Warning "略過未通過 Modern Wingman 身分驗證的程序：$DisplayName，PID $ProcessId。"
        return
    }

    if (-not $Quiet) {
        Write-Host "正在停止 $DisplayName（PID $ProcessId）..." -ForegroundColor Cyan
    }
    & taskkill.exe /PID $ProcessId /T /F 2>$null | Out-Null
    [void]$stoppedProcessIds.Add($ProcessId)
}

function Test-ViteProcess {
    <# Vite 必須同時具有 vite command 與本專案 desktop 路徑，不能只依 node.exe 判斷。 #>
    param([Parameter(Mandatory)][object]$Snapshot)

    return $Snapshot.Name -ieq "node.exe" -and
        $Snapshot.CommandLine.Contains("vite", [StringComparison]::OrdinalIgnoreCase) -and
        $Snapshot.CommandLine.Contains($desktopRoot, [StringComparison]::OrdinalIgnoreCase)
}

function Test-AgentProcess {
    <# AgentService 只接受目前 workspace 的 Debug 執行檔。 #>
    param([Parameter(Mandatory)][object]$Snapshot)

    return Test-SamePath -Left $Snapshot.ExecutablePath -Right $agentExecutable
}

function Test-TauriProcess {
    <# Tauri 只接受此 workspace .cargo/config.toml 指定 target-dir 內的執行檔。 #>
    param([Parameter(Mandatory)][object]$Snapshot)

    return Test-SamePath -Left $Snapshot.ExecutablePath -Right $tauriExecutable
}

function Test-Neo4jProcess {
    <# 只回收 .Wingman runtime 內建 Java；不得碰使用者自行安裝的 Neo4j 或其他 Java。 #>
    param([Parameter(Mandatory)][object]$Snapshot)

    return $Snapshot.Name -ieq "java.exe" -and
        -not [string]::IsNullOrWhiteSpace($Snapshot.ExecutablePath) -and
        [System.IO.Path]::GetFullPath($Snapshot.ExecutablePath).StartsWith(
            $neo4jRuntimeRoot,
            [StringComparison]::OrdinalIgnoreCase) -and
        $Snapshot.CommandLine.Contains("neo4j", [StringComparison]::OrdinalIgnoreCase)
}

function Read-RuntimeState {
    <# ownership 損壞時採安全降級：不信任內容，改以執行檔與 port 驗證。 #>
    if (-not (Test-Path -LiteralPath $runtimeStatePath)) {
        return $null
    }
    try {
        $state = Get-Content -LiteralPath $runtimeStatePath -Raw | ConvertFrom-Json
        if (-not (Test-SamePath -Left ([string]$state.WorkspaceRoot) -Right $workspaceRoot)) {
            Write-Warning "Debug runtime ownership 屬於其他 workspace，本次不會依該記錄終止程序。"
            return $null
        }
        return $state
    }
    catch {
        Write-Warning "Debug runtime ownership 無法解析，將以固定執行檔路徑清理。"
        return $null
    }
}

function Stop-OwnedViteProcesses {
    <# 優先使用 PID + StartTime；再檢查 4173，以支援 VS Code 異常退出後的自我修復。 #>
    param([object]$State)

    if ($null -ne $State) {
        foreach ($entry in @(
            @($State.ViteListenerProcessId, $State.ViteListenerStartTimeUtcTicks, "Vite listener"),
            @($State.ViteLauncherProcessId, $State.ViteLauncherStartTimeUtcTicks, "Vite launcher")
        )) {
            if ($null -eq $entry[0] -or $null -eq $entry[1]) {
                continue
            }
            $expectedTicks = [long]$entry[1]
            Stop-VerifiedProcessTree -ProcessId ([int]$entry[0]) -DisplayName ([string]$entry[2]) -Validator {
                param($snapshot)
                return $snapshot.StartTimeUtcTicks -eq $expectedTicks
            }
        }
    }

    foreach ($processId in Get-ListeningProcessIds -Port 4173) {
        Stop-VerifiedProcessTree -ProcessId $processId -DisplayName "Vite listener" -Validator ${function:Test-ViteProcess}
    }
}

function Stop-KnownDebugProcesses {
    <# Debugger 正常停止後通常找不到這些程序；此處只處理中止或當機殘留。 #>
    foreach ($processId in Get-ListeningProcessIds -Port 5002) {
        Stop-VerifiedProcessTree -ProcessId $processId -DisplayName "AgentService" -Validator ${function:Test-AgentProcess}
    }

    Get-CimInstance Win32_Process -Filter "Name = 'modern-wingman-desktop.exe'" -ErrorAction SilentlyContinue |
        ForEach-Object {
            Stop-VerifiedProcessTree -ProcessId ([int]$_.ProcessId) -DisplayName "Tauri Desktop" -Validator ${function:Test-TauriProcess}
        }
}

function Stop-ManagedNeo4j {
    <# Agent 被 Debugger 強制終止時 DisposeAsync 未必執行，因此必須顯式清理內建 Neo4j。 #>
    $neo4jProcesses = @(
        Get-CimInstance Win32_Process -Filter "Name = 'java.exe'" -ErrorAction SilentlyContinue |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
                [System.IO.Path]::GetFullPath($_.ExecutablePath).StartsWith(
                    $neo4jRuntimeRoot,
                    [StringComparison]::OrdinalIgnoreCase) -and
                ([string]$_.CommandLine).Contains("neo4j", [StringComparison]::OrdinalIgnoreCase)
            } |
            Sort-Object ParentProcessId -Descending
    )
    foreach ($process in $neo4jProcesses) {
        Stop-VerifiedProcessTree -ProcessId ([int]$process.ProcessId) -DisplayName "Managed Neo4j" -Validator ${function:Test-Neo4jProcess}
    }
}

function Wait-ForOwnedPortsToClose {
    <# 最多等十秒讓 Kestrel、Node 與 Neo4j 完成 socket release。 #>
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        $remaining = @(
            foreach ($port in $portsToVerify) {
                foreach ($processId in Get-ListeningProcessIds -Port $port) {
                    [pscustomobject]@{ Port = $port; ProcessId = $processId }
                }
            }
        )
        if ($remaining.Count -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    foreach ($item in $remaining) {
        $snapshot = Get-ProcessSnapshot -ProcessId $item.ProcessId
        $description = if ($null -eq $snapshot) {
            "程序已結束但 socket 尚在釋放"
        }
        else {
            "$($snapshot.Name)，$($snapshot.ExecutablePath)"
        }
        Write-Warning "Port $($item.Port) 仍由 PID $($item.ProcessId) 使用：$description"
    }
}

$state = Read-RuntimeState
Stop-KnownDebugProcesses
Stop-OwnedViteProcesses -State $state
Stop-ManagedNeo4j
Wait-ForOwnedPortsToClose

# 只有確認內建 Java 與 Bolt listener 都已消失，才能移除 Neo4j ownership。
# 若有程序無法停止，保留 ownership 能讓下一次啟動繼續做安全身分驗證。
$remainingManagedNeo4j = @(
    Get-CimInstance Win32_Process -Filter "Name = 'java.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
            [System.IO.Path]::GetFullPath($_.ExecutablePath).StartsWith(
                $neo4jRuntimeRoot,
                [StringComparison]::OrdinalIgnoreCase) -and
            ([string]$_.CommandLine).Contains("neo4j", [StringComparison]::OrdinalIgnoreCase)
        }
)
if ($remainingManagedNeo4j.Count -eq 0 -and @(Get-ListeningProcessIds -Port 17688).Count -eq 0) {
    Remove-Item -LiteralPath $neo4jOwnershipPath -Force -ErrorAction SilentlyContinue
}

Remove-Item -LiteralPath $viteOutputPath, $viteErrorPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $runtimeStatePath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath "$runtimeStatePath.tmp" -Force -ErrorAction SilentlyContinue

if (-not $Quiet) {
    $action = if ($ForRestart) { "啟動前清理" } else { "停止" }
    Write-Host "Modern Wingman 全端 Debug $action 完成。" -ForegroundColor Green
}
