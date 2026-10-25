#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position=0)][ValidateSet('start','ready','stop','reset','verify')][string]$Action,
    [ValidatePattern('^[a-z][a-z0-9-]{0,31}$')][string]$Name = 'local',
    [ValidateRange(1024,65518)][int]$PortBase = 18200,
    [Nullable[guid]]$DeclinedOrderId
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'lab-platform.ps1')
if ($null -ne $DeclinedOrderId -and ($Action -ne 'start' -or $DeclinedOrderId -eq [guid]::Empty)) { throw 'DeclinedOrderId requires start and a nonempty UUID.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$localRoot = Join-Path $repo '.local'
$labRoot = [IO.Path]::GetFullPath((Join-Path $localRoot $Name))
# Refuse symlink/junction traversal before any mutation or recursive deletion.
foreach ($path in @($localRoot, $labRoot)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked lab path is not allowed: $path" }
}
# Refuse linked descendants before stop-file writes, log creation or reset.
if (Test-Path -LiteralPath $labRoot) {
    $links = Get-ChildItem -LiteralPath $labRoot -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }
    if ($links) { throw 'Linked entries inside the environment are not allowed.' }
}
$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($repo.ToLowerInvariant()))).Substring(0,10).ToLowerInvariant()
$project = "otelplay-$Name-$hash"
$compose = Join-Path $repo 'infra/compose.yaml'
$ownerPath = Join-Path $labRoot 'owner.json'
$manifestPath = Join-Path $labRoot 'processes.json'
$services = @('Checkout.Api','Orders.Service','Payments.Api','Inventory.Service','Fulfillment.Service','Couriers.Api','WarehouseResponse.Service','Warehouse.Simulator')
$env:LAB_NAME = $Name
$env:LAB_RABBIT_PORT = "$PortBase"
$env:LAB_MANAGEMENT_PORT = "$($PortBase + 1)"
$env:Lab__Name = $Name
$env:Lab__Root = $labRoot
$env:Lab__RabbitPort = "$PortBase"
$env:Lab__RabbitHost = '127.0.0.1'
$env:Lab__RabbitUser = 'playground'
$env:Lab__RabbitPassword = 'local-playground-only'
function Invoke-Compose([string[]]$Arguments) {
    & docker compose --project-name $project --file $compose @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose failed: $Arguments" }
}
function Assert-Owner {
    if (!(Test-Path -LiteralPath $ownerPath)) { throw "No saved environment $Name. Run start first." }
    $owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
    if ($owner.repo -ne $repo -or $owner.project -ne $project -or $owner.portBase -ne $PortBase) { throw 'Environment ownership/port mismatch. Use the original Name and PortBase.' }
}
function Save-Processes($processes) { ConvertTo-Json -InputObject @($processes) -Depth 5 | Set-Content -LiteralPath $manifestPath }
function Stop-Hosts {
    if (!(Test-Path -LiteralPath $manifestPath)) { return }
    $processes = @(Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)
    foreach ($entry in $processes) {
        $process = Get-Process -Id $entry.pid -ErrorAction SilentlyContinue
        if (!$process) { continue }
        # A recycled PID must never target another application's process.
        Assert-ProcessIdentity $process $entry
        Set-Content -LiteralPath (Join-Path $labRoot "run/$($entry.service).stop") -Value stop
        if (!$process.WaitForExit(15000)) { throw "Host $($entry.service) did not stop gracefully; inspect its log. No unrelated process was killed." }
        $logPath = Join-Path $labRoot "logs/$($entry.service).log"
        if (!(Select-String -LiteralPath $logPath -SimpleMatch "Host stopped")) { throw "Missing shutdown log for $($entry.service); inspect host output." }
    }
    Remove-Item -LiteralPath $manifestPath
}
function Assert-Ready {
    foreach ($i in 0..($services.Count - 1)) {
        $result = Invoke-RestMethod "http://127.0.0.1:$($PortBase + 10 + $i)/health/ready" -TimeoutSec 3
        if ($result.service -ne $services[$i] -or $result.status -ne 'ready') { throw "Unexpected host on port $($PortBase + 10 + $i)" }
        Write-Output "$($services[$i]): ready"
    }
}
Push-Location $repo
try {
    switch ($Action) {
        'start' {
            Assert-LabPrerequisites $repo
            if (Test-Path -LiteralPath $ownerPath) { Assert-Owner }
            if (Test-Path -LiteralPath $manifestPath) { throw 'Saved processes exist. Use ready or stop before starting again.' }
            foreach ($service in $services) {
                if (!(Test-Path "src/$service/bin/Release/net10.0/$service.dll")) { throw 'Run dotnet build -c Release first.' }
            }
            # Reserve/check every port before creating this environment; never attach to an existing broker.
            $listeners = @()
            try {
                foreach ($port in @($PortBase, ($PortBase + 1)) + @(10..17 | ForEach-Object { $PortBase + $_ })) {
                    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
                    $listener.Server.ExclusiveAddressUse = $true
                    try { $listener.Start(); $listeners += $listener }
                    catch { $listener.Stop(); throw "Port $port is unavailable. Choose a free Name/PortBase pair; do not stop another lab. $($_.Exception.Message)" }
                }
            } finally { foreach ($listener in $listeners) { $listener.Stop() } }
            New-Item -ItemType Directory -Force $labRoot, (Join-Path $labRoot 'logs'), (Join-Path $labRoot 'run') | Out-Null
            @{ repo=$repo; project=$project; portBase=$PortBase } | ConvertTo-Json | Set-Content -LiteralPath $ownerPath
            $processes = @()
            try {
                Invoke-Compose @('up','-d')
                $deadline = [DateTime]::UtcNow.AddSeconds(120)
                do {
                    $container = & docker compose --project-name $project --file $compose ps -q rabbitmq
                    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect broker.' }
                    $health = if ($container) { & docker inspect --format '{{.State.Health.Status}}' $container } else { '' }
                    if ($health -eq 'healthy') { break }
                    Start-Sleep -Seconds 2
                } while ([DateTime]::UtcNow -lt $deadline)
                if ($health -ne 'healthy') { throw 'RabbitMQ did not become healthy within 120 seconds.' }
                foreach ($i in 0..($services.Count - 1)) {
                    $service = $services[$i]
                    $env:ASPNETCORE_URLS = "http://127.0.0.1:$($PortBase + 10 + $i)"
                    $dll = Join-Path $repo "src/$service/bin/Release/net10.0/$service.dll"
                    $runner = Join-Path $PSScriptRoot 'run-host.ps1'
                    $logPath = Join-Path $labRoot "logs/$service.log"
                    $errorLogPath = Join-Path $labRoot "logs/$service.err.log"
                    # Optional US10 scenario is scoped to this child process only.
                    $savedProfile = [Environment]::GetEnvironmentVariable('Simulation__Profile')
                    $savedOrder = [Environment]::GetEnvironmentVariable('Simulation__OrderId')
                    try {
                        if ($service -eq 'Payments.Api' -and $null -ne $DeclinedOrderId) {
                            $env:Simulation__Profile = 'payment-declined'
                            $env:Simulation__OrderId = $DeclinedOrderId.ToString()
                        }
                        $process = Start-LabProcess -Executable (Get-Process -Id $PID).Path `
                            -Arguments @('-NoProfile', '-File', $runner, '-Dll', $dll, '-LogPath', $logPath, '-ErrorLogPath', $errorLogPath) `
                            -WorkingDirectory $repo -OutputPath (Join-Path $labRoot "logs/$service.runner.log") `
                            -ErrorPath (Join-Path $labRoot "logs/$service.runner.err.log")
                    } finally {
                        # PowerShell can coerce $null to an empty string for this
                        # .NET call; an absent setting must remain absent.
                        if ($null -eq $savedProfile) { Remove-Item Env:Simulation__Profile -ErrorAction SilentlyContinue }
                        else { $env:Simulation__Profile = $savedProfile }
                        if ($null -eq $savedOrder) { Remove-Item Env:Simulation__OrderId -ErrorAction SilentlyContinue }
                        else { $env:Simulation__OrderId = $savedOrder }
                    }
                    $process = Get-Process -Id $process.Id
                    $processes += @{ pid=$process.Id; service=$service; identity=(Get-ProcessIdentity $process); executable=$process.Path }
                    Save-Processes $processes
                }
                $deadline = [DateTime]::UtcNow.AddSeconds(60)
                do {
                    try { Assert-Ready; return } catch { $lastFailure = $_ }
                    Start-Sleep -Seconds 1
                } while ([DateTime]::UtcNow -lt $deadline)
                throw "Hosts did not become ready: $lastFailure"
            } catch {
                $failure = $_
                try { Stop-Hosts } catch { Write-Warning "Host cleanup failed: $_" }
                try { Invoke-Compose @('down') } catch { Write-Warning "Broker cleanup failed: $_" }
                throw $failure
            }
        }
        'ready' { Assert-Owner; Assert-Ready }
        'stop' { Assert-Owner; Stop-Hosts; Invoke-Compose @('down') }
        'reset' {
            Assert-Owner
            Stop-Hosts
            Invoke-Compose @('down','--volumes')
            $expected = [IO.Path]::GetFullPath((Join-Path $repo ".local/$Name"))
            if ($labRoot -ne $expected -or !(Test-Path -LiteralPath $ownerPath)) { throw 'Unsafe reset path.' }
            $links = Get-ChildItem -LiteralPath $labRoot -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }
            if ($links) { throw 'Reset refuses linked entries inside the environment.' }
            Remove-Item -LiteralPath $labRoot -Recurse -Force
            Write-Output "Removed only $project and $labRoot"
        }
        'verify' {
            Assert-Owner
            Assert-Ready
            $env:PLAYGROUND_BROKER_TEST = '1'
            try {
                & dotnet test --no-build -c Release
                if ($LASTEXITCODE -ne 0) { throw 'Foundation tests failed.' }
            } finally { Remove-Item Env:PLAYGROUND_BROKER_TEST -ErrorAction SilentlyContinue }
            foreach ($service in $services) {
                if (!(Select-String -LiteralPath (Join-Path $labRoot "logs/$service.log") -SimpleMatch 'Host started')) { throw "Missing ILogger/Serilog startup for $service" }
                if (!(Test-Path -LiteralPath (Join-Path $labRoot "data/$service/state.db"))) { throw "Missing database for $service" }
            }
            foreach ($folder in 'requests','responses','processed','errors') {
                $probe = Join-Path $labRoot "warehouse/$folder/.verify-$([Guid]::NewGuid().ToString('N'))"
                Set-Content -LiteralPath $probe -Value 'directory-probe'
                if ((Get-Content -LiteralPath $probe) -ne 'directory-probe') { throw 'Directory read/write failed.' }
                Remove-Item -LiteralPath $probe -Force
            }
            $response = Invoke-WebRequest "http://127.0.0.1:$($PortBase+10)/api/v1/orders" -Method Post -ContentType 'application/json' -Body '{}' -SkipHttpErrorCheck
            if ($response.StatusCode -ne 400 -or ($response.Content | ConvertFrom-Json).code -ne 'invalid_request') { throw 'Checkout must reject an invalid request using the v1 error contract.' }
            Write-Output 'Laboratory verified, including real RabbitMQ and checkout validation. Complete ecommerce remains covered by its own stories.'
        }
    }
} finally { Pop-Location }
