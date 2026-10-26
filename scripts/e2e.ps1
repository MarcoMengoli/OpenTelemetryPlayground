#requires -Version 7.2
[CmdletBinding()]
param(
    [ValidatePattern('^[a-z][a-z0-9-]{0,28}$')][string]$Name = 'us10-a',
    [ValidateRange(1024,65518)][int]$PortBase = 18500,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'lab-platform.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repo
try { Assert-LabPrerequisites $repo } finally { Pop-Location }
$localRoot = Join-Path $repo '.local'
$evidenceRoot = Join-Path $localRoot 'e2e-evidence'
foreach ($path in @($localRoot, $evidenceRoot)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked evidence path is not allowed: $path" }
}
$evidence = Join-Path $evidenceRoot "$Name-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $evidence | Out-Null
$dll = Join-Path $repo 'tests/Playground.EndToEnd/bin/Release/net10.0/Playground.EndToEnd.dll'
# Save only this shell's environment; other running hosts retain their own copy.
$keys = @('LAB_NAME','LAB_RABBIT_PORT','LAB_MANAGEMENT_PORT','Lab__Name','Lab__Root','Lab__RabbitHost','Lab__RabbitPort','Lab__RabbitUser','Lab__RabbitPassword','ASPNETCORE_URLS',
    'Simulation__Profile','Simulation__DelayMs','Simulation__FailuresBeforeSuccess','Simulation__OrderId','Simulation__WarehouseReference',
    'Services__OrdersBaseUrl','Services__PaymentsBaseUrl','Services__FulfillmentBaseUrl','Services__CouriersUrl')
$saved = @{}
foreach ($key in $keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key) }
$runs = @()
Push-Location $repo
try {
    if (!$NoBuild) {
        & dotnet restore --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
        & dotnet build --no-restore -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
    if (!(Test-Path -LiteralPath $dll)) { throw 'Missing end-to-end verifier: build Release first.' }
    $env:Simulation__Profile = 'happy-path'
    $env:Simulation__DelayMs = '0'
    $env:Simulation__FailuresBeforeSuccess = '0'
    Remove-Item Env:Simulation__OrderId, Env:Simulation__WarehouseReference -ErrorAction SilentlyContinue
    $env:Services__OrdersBaseUrl = "http://127.0.0.1:$($PortBase+11)"
    $env:Services__PaymentsBaseUrl = "http://127.0.0.1:$($PortBase+12)"
    $env:Services__FulfillmentBaseUrl = "http://127.0.0.1:$($PortBase+14)"
    $env:Services__CouriersUrl = "http://127.0.0.1:$($PortBase+15)"
    foreach ($iteration in 1..2) {
        $runName = "$Name-$iteration"
        $labRoot = Join-Path $localRoot $runName
        # Never reuse or reset an environment that existed before this invocation.
        if (Test-Path -LiteralPath $labRoot) { throw "E2E requires a fresh namespace; $labRoot already exists. Use another Name or inspect it manually." }
        $destination = Join-Path $evidence "run-$iteration"
        New-Item -ItemType Directory -Path $destination | Out-Null
        $report = Join-Path $destination 'result.json'
        $failure = $null
        $declined = [guid]::NewGuid()
        try {
            & (Join-Path $PSScriptRoot 'lab.ps1') start -Name $runName -PortBase $PortBase -DeclinedOrderId $declined
            & (Join-Path $PSScriptRoot 'lab.ps1') ready -Name $runName -PortBase $PortBase
            & dotnet $dll --E2E:DeclinedOrderId $declined.ToString() --E2E:Report $report 2>&1 | Tee-Object -FilePath (Join-Path $destination 'verification.log')
            if ($LASTEXITCODE -ne 0) { throw "Business verification failed in $runName. Evidence: $destination" }
            $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
            if (!$result.succeeded -or $result.environment -ne $runName) { throw 'Verifier result missing or inconsistent.' }
            $runs += $result
        } catch { $failure = $_ }
        finally {
            # start may fail after creating its owner. Base commands still verify
            # worktree/project/ports/PID identity before stopping or removing anything.
            if (Test-Path -LiteralPath (Join-Path $labRoot 'owner.json')) {
                try { & (Join-Path $PSScriptRoot 'lab.ps1') stop -Name $runName -PortBase $PortBase }
                catch { if ($null -eq $failure) { $failure = $_ }; Write-Warning "Stop failed: $_" }
                try {
                    $links = Get-ChildItem -LiteralPath $labRoot -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }
                    if ($links) { throw 'Evidence collection refuses linked lab entries.' }
                    foreach ($folder in 'logs','data','warehouse') {
                        $source = Join-Path $labRoot $folder
                        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $destination $folder) -Recurse }
                    }
                } catch { if ($null -eq $failure) { $failure = $_ }; Write-Warning "Evidence collection failed: $_" }
                try { & (Join-Path $PSScriptRoot 'lab.ps1') reset -Name $runName -PortBase $PortBase }
                catch { if ($null -eq $failure) { $failure = $_ }; Write-Warning "Reset failed: $_" }
                if (Test-Path -LiteralPath $labRoot) { if ($null -eq $failure) { $failure = "Environment $runName was not reset." } }
            }
        }
        if ($null -ne $failure) { throw $failure }
    }
    if ($runs.Count -ne 2 -or $runs[0].orderId -eq $runs[1].orderId -or $runs[0].environment -eq $runs[1].environment -or $runs[0].declinedOrderId -eq $runs[1].declinedOrderId) { throw 'Two independent successful runs were not observed.' }
    @{ succeeded=$true; runs=$runs; evidence=$evidence } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $evidence 'summary.json')
    Write-Output "Full ecommerce verified twice with real services; environments reset. Evidence: $evidence"
} finally {
    foreach ($key in $keys) {
        if ($null -eq $saved[$key]) { Remove-Item -LiteralPath "Env:$key" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    }
    Pop-Location
}
