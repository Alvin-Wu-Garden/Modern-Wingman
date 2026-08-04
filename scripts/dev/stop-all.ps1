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
$tauriRoot = Join-Path $desktopRoot "src-tauri"
$agentExecutable = [System.IO.Path]::GetFullPath(
    (Join-Path $workspaceRoot "apps\agent-service\bin\Debug\net10.0\AgentService.exe"))
$agentAssembly = [System.IO.Path]::GetFullPath(
    (Join-Path $workspaceRoot "apps\agent-service\bin\Debug\net10.0\AgentService.dll"))

# Cargo 的 target-dir 可能由 workspace 的 .cargo/config.toml 指定在工作區外，
# 因此透過 cargo metadata 取得實際路徑，避免綁定特定電腦或使用者目錄。
$tauriTargetDirectory = $null
try {
    $cargoMetadataJson = & cargo metadata `
        --no-deps `
        --format-version 1 `
        --manifest-path (Join-Path $tauriRoot "Cargo.toml") `
        2>$null | Out-String
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($cargoMetadataJson)) {
        $tauriTargetDirectory = [string](
            ($cargoMetadataJson | ConvertFrom-Json).target_directory)
    }
}
catch {
    # 停止流程仍可在 cargo 尚未安裝時清理 Agent/Vite；Tauri 路徑退回預設 target。
}

if ([string]::IsNullOrWhiteSpace($tauriTargetDirectory)) {
    $tauriTargetDirectory = Join-Path $tauriRoot "target"
}

$tauriExecutable = [System.IO.Path]::GetFullPath(
    (Join-Path $tauriTargetDirectory "debug\modern-wingman-desktop.exe"))
$neo4jRuntimeRoot = [System.IO.Path]::GetFullPath(
    (Join-Path ([Environment]::GetFolderPath("UserProfile")) ".Wingman\neo4j"))
$neo4jOwnershipPath = Join-Path $neo4jRuntimeRoot "managed-v3-owner.json"
$runtimeStatePath = Join-Path $env:TEMP "modern-wingman-debug-runtime.json"
$viteOutputPath = Join-Path $env:TEMP "modern-wingman-vite.stdout.log"
$viteErrorPath = Join-Path $env:TEMP "modern-wingman-vite.stderr.log"
# 只驗證本流程明確管理的 Listener；Neo4j Browser HTTP port 不在此清單。
$portsToVerify = @(4173, 5002, 17688)
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

function Test-AgentProcess {
    <# AgentService 可由 VS Code 以 exe 或 dotnet + DLL 啟動，兩者都必須屬於目前 workspace。 #>
    param([Parameter(Mandatory)][object]$Snapshot)

    if (Test-SamePath -Left $Snapshot.ExecutablePath -Right $agentExecutable) {
        return $true
    }

    return $Snapshot.Name -ieq "dotnet.exe" -and
        $Snapshot.CommandLine.Contains($agentAssembly, [StringComparison]::OrdinalIgnoreCase)
}

function Test-TauriProcess {
    <# Tauri 只接受此 workspace .cargo/config.toml 指定 target-dir 內的執行檔。 #>
    param([Parameter(Mandatory)][object]$Snapshot)

    return Test-SamePath -Left $Snapshot.ExecutablePath -Right $tauriExecutable
}

function Read-ManagedNeo4jOwnership {
    <# 讀取 Agent Service 建立的 ownership；檔案遺失或損壞時不得猜測並終止 Java。 #>
    if (-not (Test-Path -LiteralPath $neo4jOwnershipPath)) {
        return $null
    }

    try {
        return Get-Content -LiteralPath $neo4jOwnershipPath -Raw | ConvertFrom-Json
    }
    catch {
        Write-Warning "Neo4j ownership 無法解析，略過 managed Neo4j 清理以避免誤殺程序。"
        return $null
    }
}

function Test-Neo4jLauncher {
    <# launcher 必須同時指向 ownership 的安裝路徑與 neo4j 指令，避免 PID 重用誤判。 #>
    param(
        [Parameter(Mandatory)][object]$Snapshot,
        [Parameter(Mandatory)][string]$Home
    )

    return ($Snapshot.Name -ieq "cmd.exe" -or
            $Snapshot.Name -ieq "powershell.exe" -or
            $Snapshot.Name -ieq "pwsh.exe") -and
        $Snapshot.CommandLine.Contains("neo4j", [StringComparison]::OrdinalIgnoreCase) -and
        $Snapshot.CommandLine.Contains($Home, [StringComparison]::OrdinalIgnoreCase)
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
    <# 只依 runtime manifest 回收 Vite；沒有 manifest 時，4173 視為未知程序不得誤殺。 #>
    param([object]$State)

    if ($null -eq $State) {
        return
    }

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
    <# Agent 被 Debugger 強制終止時 DisposeAsync 未必執行；只能依 ownership launcher 回收整棵程序樹。 #>
    $ownership = Read-ManagedNeo4jOwnership
    if ($null -eq $ownership) {
        return
    }

    if (-not [string]::Equals(
            [string]$ownership.Endpoint,
            "127.0.0.1:17688",
            [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-SamePath -Left ([string]$ownership.Home) -Right $neo4jRuntimeRoot)) {
        Write-Warning "Neo4j ownership 不屬於目前固定 endpoint 或 runtime root，略過清理。"
        return
    }

    $launcherSnapshot = Get-ProcessSnapshot -ProcessId ([int]$ownership.LauncherProcessId)
    if ($null -eq $launcherSnapshot -or
        $launcherSnapshot.StartTimeUtcTicks -ne [long]$ownership.LauncherProcessStartTimeUtcTicks) {
        Write-Warning "Neo4j ownership launcher 已結束或 PID 已重用，保留 ownership 並略過清理。"
        return
    }

    Stop-VerifiedProcessTree `
        -ProcessId ([int]$ownership.LauncherProcessId) `
        -DisplayName "Managed Neo4j" `
        -Validator {
            param($snapshot)
            return Test-Neo4jLauncher -Snapshot $snapshot -Home ([string]$ownership.Home)
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

# 只有 ownership launcher 已不存在且 Bolt listener 已釋放，才能移除 ownership。
# 若仍有 listener，可能是 launcher 的 Java 子程序尚未收尾，必須保留記錄供下次安全接管。
$remainingOwnership = Read-ManagedNeo4jOwnership
$ownershipLauncherAlive = $false
if ($null -ne $remainingOwnership) {
    $remainingLauncher = Get-ProcessSnapshot -ProcessId ([int]$remainingOwnership.LauncherProcessId)
    $ownershipLauncherAlive = $null -ne $remainingLauncher -and
        $remainingLauncher.StartTimeUtcTicks -eq [long]$remainingOwnership.LauncherProcessStartTimeUtcTicks
}
if (-not $ownershipLauncherAlive -and @(Get-ListeningProcessIds -Port 17688).Count -eq 0) {
    Remove-Item -LiteralPath $neo4jOwnershipPath -Force -ErrorAction SilentlyContinue
}

Remove-Item -LiteralPath $viteOutputPath, $viteErrorPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $runtimeStatePath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath "$runtimeStatePath.tmp" -Force -ErrorAction SilentlyContinue

if (-not $Quiet) {
    $action = if ($ForRestart) { "啟動前清理" } else { "停止" }
    Write-Host "Modern Wingman 全端 Debug $action 完成。" -ForegroundColor Green
}
