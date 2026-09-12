# Stores the MiniMax API/Subscription token in AgentLimits config, encrypted with DPAPI (CurrentUser).
# Usage: powershell -File set-minimax-token.ps1 -TokenFile <path>
# The token file is deleted after a successful write.
param(
    [Parameter(Mandatory = $true)][string]$TokenFile
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Security

$token = ([IO.File]::ReadAllText($TokenFile)).Trim()
if (-not $token) { throw "token file is empty" }

$bytes = [Text.Encoding]::UTF8.GetBytes($token)
$blob = [Security.Cryptography.ProtectedData]::Protect($bytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
$b64 = [Convert]::ToBase64String($blob)

$dir = Join-Path $env:LOCALAPPDATA "AgentLimits"
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$cfgPath = Join-Path $dir "config.json"

$cfg = $null
if (Test-Path $cfgPath) {
    try { $cfg = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $cfg = $null }
}
if ($null -eq $cfg) {
    $cfg = [pscustomobject]@{
        WindowLeft         = $null
        WindowTop          = $null
        Zai                = [pscustomobject]@{ TokenProtected = $null; Organization = $null; Project = $null }
        Minimax            = [pscustomobject]@{ TokenProtected = $null }
        AgyIntervalSec     = 300
        CodexIntervalSec   = 60
        ClaudeIntervalSec  = 300
        ZaiIntervalSec     = 180
        MinimaxIntervalSec = 180
    }
}
if ($null -eq $cfg.Minimax) {
    $cfg | Add-Member -NotePropertyName Minimax -NotePropertyValue ([pscustomobject]@{}) -Force
}

$cfg.Minimax | Add-Member -NotePropertyName TokenProtected -NotePropertyValue $b64 -Force

$cfg | ConvertTo-Json -Depth 6 | Out-File -FilePath $cfgPath -Encoding utf8
Remove-Item $TokenFile -Force

Write-Output "saved: $cfgPath"
