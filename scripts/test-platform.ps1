#requires -Version 7.2
# Offline regression checks: real child processes, no Docker or SDK required.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'lab-platform.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root = Join-Path ([IO.Path]::GetTempPath()) "playground space '$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root | Out-Null
$child = $null
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Must-Refuse([scriptblock]$Action, [string]$Message) {
    $refused = $false
    try { & $Action } catch { $refused = $_.ToString().Contains($Message) }
    Assert $refused "Expected refusal containing: $Message"
}
try {
    $worker = Join-Path $root 'child with spaces.ps1'
    @'
param([string]$Root, [string]$Value)
$ErrorActionPreference = 'Stop'
$Value | Set-Content -LiteralPath (Join-Path $Root 'received.txt')
Write-Output 'child stdout'
[Console]::Error.WriteLine('child stderr')
$deadline = [DateTime]::UtcNow.AddSeconds(60)
while (!(Test-Path -LiteralPath (Join-Path $Root 'stop'))) {
    if ([DateTime]::UtcNow -gt $deadline) { exit 2 }
    Start-Sleep -Milliseconds 50
}
'@ | Set-Content -LiteralPath $worker
    # A separate launcher exits before we inspect/stop the surviving process.
    $launcher = Join-Path $root 'launcher.ps1'
    @'
param([string]$Platform, [string]$Root, [string]$Worker)
$ErrorActionPreference = 'Stop'
. $Platform
$p = Start-LabProcess -Executable (Get-Process -Id $PID).Path -Arguments @('-NoProfile','-File',$Worker,'-Root',$Root,'-Value','literal space '' $ & value') -WorkingDirectory $Root -OutputPath (Join-Path $Root 'stdout.log') -ErrorPath (Join-Path $Root 'stderr.log')
@{pid=$p.Id; identity=(Get-ProcessIdentity $p); executable=$p.Path; service='test-child'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Root 'process.json')
'@ | Set-Content -LiteralPath $launcher
    & (Get-Process -Id $PID).Path -NoProfile -File $launcher -Platform (Join-Path $PSScriptRoot 'lab-platform.ps1') -Root $root -Worker $worker
    Assert ($LASTEXITCODE -eq 0) 'Launcher failed.'
    $entry = Get-Content -LiteralPath (Join-Path $root 'process.json') -Raw | ConvertFrom-Json
    $child = Get-Process -Id $entry.pid
    Assert-ProcessIdentity $child $entry
    Start-Sleep -Milliseconds 500
    $child.Refresh()
    Assert (!$child.HasExited) 'Child did not survive launcher exit.'
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (!(Test-Path -LiteralPath (Join-Path $root 'received.txt'))) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Child did not receive paths/arguments.' }
        Start-Sleep -Milliseconds 50
    }
    Assert ((Get-Content -LiteralPath (Join-Path $root 'received.txt')) -ceq 'literal space '' $ & value') 'Argument was split or evaluated.'
    $identity = $entry.identity
    $entry.identity = 'recycled-pid'
    Must-Refuse { Assert-ProcessIdentity $child $entry } 'PID ownership mismatch'
    $entry.identity = $identity
    $executable = $entry.executable
    $entry.executable = 'unrelated executable'
    Must-Refuse { Assert-ProcessIdentity $child $entry } 'PID ownership mismatch'
    $entry.executable = $executable
    Assert-ProcessIdentity (Get-Process -Id $entry.pid) $entry
    'stop' | Set-Content -LiteralPath (Join-Path $root 'stop')
    Assert ($child.WaitForExit(10000)) 'Owned child did not stop.'
    if (!$IsWindows) {
        Assert ((Get-Content -LiteralPath (Join-Path $root 'stdout.log') -Raw).Contains('child stdout')) 'Detached stdout missing.'
        Assert ((Get-Content -LiteralPath (Join-Path $root 'stderr.log') -Raw).Contains('child stderr')) 'Detached stderr missing.'
    }
    $hostLog = Join-Path $root 'host.log'
    [IO.File]::WriteAllText($hostLog, "Host stopped in previous run`n")
    $offset = (Get-Item -LiteralPath $hostLog).Length
    [IO.File]::AppendAllText($hostLog, "Host started in current run`n")
    Must-Refuse { Assert-HostShutdown $hostLog $offset } 'Missing shutdown log for this run'
    [IO.File]::AppendAllText($hostLog, "Host stopped in current run`n")
    Assert-HostShutdown $hostLog $offset
    # A copied script root exercises the actual owner/link refusal before Docker.
    $copy = Join-Path $root 'checkout with spaces'
    New-Item -ItemType Directory -Path (Join-Path $copy 'scripts') | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'lab.ps1'), (Join-Path $PSScriptRoot 'lab-platform.ps1') -Destination (Join-Path $copy 'scripts')
    $lab = Join-Path $copy '.local/owned'
    New-Item -ItemType Directory -Path $lab | Out-Null
    @{repo='some other checkout';project='other';portBase=18800} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $lab 'owner.json')
    'keep' | Set-Content -LiteralPath (Join-Path $lab 'sentinel')
    Must-Refuse { & (Join-Path $copy 'scripts/lab.ps1') reset -Name owned -PortBase 18800 } 'ownership/port mismatch'
    Assert (Test-Path -LiteralPath (Join-Path $lab 'sentinel')) 'Foreign owner data was removed.'
    if (!$IsWindows) {
        New-Item -ItemType SymbolicLink -Path (Join-Path $lab 'run') -Target $root | Out-Null
        Must-Refuse { & (Join-Path $copy 'scripts/lab.ps1') stop -Name owned -PortBase 18800 } 'Linked entries'
        Remove-Item -LiteralPath (Join-Path $lab 'run')
    }
    Write-Output 'PASS: detached process, literal paths/arguments, identity across shells, PID/executable mismatch, owner refusal and current-run shutdown log. No real broker/business verification.'
    if ($IsWindows) { Write-Output 'NOT RUN: symbolic-link refusal on Windows (requires additional privileges).' }
    else { Write-Output 'PASS: Unix symbolic-link refusal.' }
} finally {
    # Only the process created by this test; never search or kill by process name.
    if ($null -ne $child) {
        $child.Refresh()
        if (!$child.HasExited) { $child.Kill(); $child.WaitForExit() }
    }
    Remove-Item -LiteralPath $root -Recurse -Force
}
