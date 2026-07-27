# check-tools.ps1 — Verify all required tools are installed
$errors = @()

function Check-Tool {
    param($Name, $Command, $MinVersion)
    try {
        $ver = Invoke-Expression $Command 2>$null
        Write-Host "  [OK] $Name`: $ver"
    } catch {
        Write-Host "  [MISSING] $Name"
        $script:errors += $Name
    }
}

Write-Host "`n=== Modern Wingman Tool Check ===`n"
Check-Tool "Node.js"   "node --version"    "24"
Check-Tool "pnpm"      "pnpm.cmd --version"    "9"
Check-Tool ".NET SDK"  "dotnet --version"  "10"
Check-Tool "Git"       "git --version"     "2"
Check-Tool "Rust"      "rustc --version"   "1"
Check-Tool "Cargo"     "cargo --version"   "1"
Check-Tool "Tauri CLI" "tauri --version"   "2"

if ($errors.Count -gt 0) {
    Write-Host "`n[ERROR] Missing tools: $($errors -join ', ')" -ForegroundColor Red
    exit 1
} else {
    Write-Host "`n[OK] All tools present.`n" -ForegroundColor Green
}
