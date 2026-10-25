# From checkout to a simulated invoice

This is the manual exploration path. It leaves your environment running on success
**and on failure**, so you can inspect it. Only the explicitly labeled automated
checks clean up their private environments. See the [README prerequisites](../README.md#platforms-and-prerequisites)
for installation and platform verification limits.

## Prepare your shell

Install the pinned SDK, PowerShell and Docker; start the Linux container engine.
Clone and enter the repository as shown in the README. If you already started
`explore-manual` on base 18900, stop it first or select another free base below.

**Shell: zsh (macOS Terminal). Working directory: repository root. This command
opens PowerShell in the same directory; all later PowerShell blocks use this session.**

```zsh
pwsh -NoProfile
```

Windows/Linux users can open PowerShell 7 directly. This is not zsh syntax:
`$env:Name`, `Invoke-RestMethod`, and backtick line continuation belong to PowerShell.

**Shell: PowerShell 7. Working directory: repository root.**

```powershell
dotnet restore --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
dotnet build --no-restore -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
dotnet test --no-build -c Release
if ($LASTEXITCODE -ne 0) { throw 'Offline tests failed' }
```

Skipped broker tests do not verify real messaging. Do not continue after a failed
command. Use a dedicated shell without settings inherited from a different lab.

## Manual order to invoice

**Shell: PowerShell 7. Working directory: repository root. Copy the entire block
into the same session.** It creates a fresh `guide-<random>` name, uses port base
18900 (AMQP 18900, management 18901, HTTP 18910–18917), and selects happy-path
settings. Change `$portBase` before running if those ports are occupied.
The generated name, OrderId and original port base are printed for later use.

<!-- delivery-example:start -->
```powershell
$ErrorActionPreference = 'Stop'
$name = 'guide-' + [guid]::NewGuid().ToString('N').Substring(0,10)
$portBase = 18900
$root = Join-Path (Get-Location) ".local/$name"
if (Test-Path -LiteralPath $root) { throw 'The walkthrough requires a fresh environment' }
$env:Simulation__Profile = 'happy-path'
$env:Simulation__DelayMs = '0'
$env:Simulation__FailuresBeforeSuccess = '0'
Remove-Item Env:Simulation__OrderId, Env:Simulation__WarehouseReference -ErrorAction SilentlyContinue
$env:Services__OrdersBaseUrl = "http://127.0.0.1:$($portBase+11)"
$env:Services__PaymentsBaseUrl = "http://127.0.0.1:$($portBase+12)"
$env:Services__FulfillmentBaseUrl = "http://127.0.0.1:$($portBase+14)"
$env:Services__CouriersUrl = "http://127.0.0.1:$($portBase+15)"
$orderId = [guid]::NewGuid().ToString()
$checkout = "http://127.0.0.1:$($portBase+10)/api/v1/orders"
$body = @{
    orderId = $orderId
    items = @(@{sku='BOOK'; quantity=2; unitPrice=12.50}, @{sku='PEN'; quantity=3; unitPrice=1.50})
    shippingAddress = @{recipient='Ada'; street='Via Roma 1'; city='Bologna'; postalCode='40100'; countryCode='IT'}
} | ConvertTo-Json -Depth 6
$snapshot = $null
Write-Host "Keep these values for later: name=$name portBase=$portBase orderId=$orderId"
try {
    ./scripts/lab.ps1 start -Name $name -PortBase $portBase
    ./scripts/lab.ps1 ready -Name $name -PortBase $portBase
    $accepted = Invoke-WebRequest $checkout -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 5
    Write-Host "HTTP $($accepted.StatusCode), Location $($accepted.Headers.Location), body $($accepted.Content)"
    if ($accepted.StatusCode -ne 202 -or $accepted.Headers.Location -ne "/api/v1/orders/$orderId" -or ($accepted.Content | ConvertFrom-Json).orderId -ne $orderId) { throw 'Unexpected acceptance' }
    $deadline = [DateTime]::UtcNow.AddSeconds(180)
    do {
        $snapshot = Invoke-RestMethod "$checkout/$orderId" -TimeoutSec 5
        Write-Host "Order $orderId : $($snapshot.status)"
        if ($snapshot.orderId -ne $orderId -or $snapshot.status -eq 'PaymentDeclined') { throw 'Unexpected order/outcome' }
        if ($snapshot.status -eq 'Completed') { break }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Deadline: order not completed' }
        Start-Sleep -Milliseconds 200
    } while ($true)
    if (!$snapshot.authorizationId -or !$snapshot.shipmentId -or !$snapshot.captureId -or !$snapshot.invoiceId -or $snapshot.lastError) { throw 'Incomplete completion' }
    $payment = Invoke-RestMethod "http://127.0.0.1:$($portBase+12)/api/v1/payments/$orderId" -TimeoutSec 5
    if ($payment.status -ne 'Invoiced' -or $payment.orderId -ne $orderId -or
        $payment.authorizationId -ne $snapshot.authorizationId -or $payment.captureId -ne $snapshot.captureId -or
        $payment.invoiceId -ne $snapshot.invoiceId -or $payment.total.amount -ne 29.50) { throw 'Payment reconciliation failed' }
    # Completed is not a barrier for Inventory. Inspect its database read-only,
    # waiting up to 60 seconds for this specific order, using existing .NET dependencies.
    $inventoryJson = & dotnet ./tests/Playground.EndToEnd/bin/Release/net10.0/Playground.EndToEnd.dll `
        --Lab:Root $root --E2E:InventoryOrderId $orderId
    if ($LASTEXITCODE -ne 0) { throw 'Inventory inspection failed or timed out' }
    $inventory = $inventoryJson | ConvertFrom-Json
    if (!$inventory.completed -or $inventory.orderId -ne $orderId -or $inventory.reservations.Count -ne 2 -or
        $inventory.reservations[0].sku -ne 'BOOK' -or $inventory.reservations[0].quantity -ne 2 -or
        $inventory.reservations[1].sku -ne 'PEN' -or $inventory.reservations[1].quantity -ne 3) { throw 'Unexpected Inventory reservations' }
    $inventoryJson | Write-Host
    $retry = Invoke-WebRequest $checkout -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 5
    $again = Invoke-RestMethod "$checkout/$orderId" -TimeoutSec 5
    if ($retry.StatusCode -ne 202 -or $again.status -ne 'Completed' -or $again.invoiceId -ne $snapshot.invoiceId) { throw 'Retry was not idempotent' }
    $snapshot | ConvertTo-Json -Depth 6 | Write-Host
    Get-ChildItem -LiteralPath (Join-Path $root 'warehouse/processed') -Recurse -Filter '*.json' |
        ForEach-Object { Write-Host $_.FullName; Get-Content -LiteralPath $_.FullName | Write-Host }
    Get-ChildItem -LiteralPath (Join-Path $root 'logs') -Filter '*.log' |
        Select-String -SimpleMatch $orderId | ForEach-Object { Write-Host $_.Line }
    Write-Host "WALKTHROUGH OK: $orderId, invoice $($snapshot.invoiceId), environment $name"
} catch {
    $snapshot | ConvertTo-Json -Depth 6 | Write-Host
    if (Test-Path -LiteralPath (Join-Path $root 'logs')) {
        Get-ChildItem -LiteralPath (Join-Path $root 'logs') -Filter '*.log' |
            ForEach-Object { Write-Host $_.Name; Get-Content -LiteralPath $_.FullName -Tail 30 | Write-Host }
    }
    throw
}
```
<!-- delivery-example:end -->

Expected results:

- POST returns **202** with `Location: /api/v1/orders/<OrderId>` and the matching ID.
- Polling reaches **Completed** with authorization, shipment, capture and invoice
  IDs and no `lastError`. Rapid intermediate states may not appear in the poll.
- Payments is **Invoiced**, amount **29.50 EUR**, with the same IDs as Orders.
- The independent Inventory query reports `completed: true`, BOOK quantity 2 and
  PEN quantity 3. No Inventory HTTP business endpoint is invented.
- Retrying the identical request returns 202 with the original acceptance; the
  completed order retains the same invoice ID. Reusing an ID with changed content
  returns a conflict instead.

The command does not call capture/invoice directly: Orders does that after shipment
confirmation. A 202 or a ready host alone does not satisfy these checks.

## Explore the running environment

**Shell: the same PowerShell 7 session. Working directory: repository root.**

```powershell
Invoke-RestMethod "$checkout/$orderId" | ConvertTo-Json -Depth 6
Invoke-RestMethod "http://127.0.0.1:$($portBase+12)/api/v1/payments/$orderId" | ConvertTo-Json -Depth 6
Get-Content -LiteralPath (Join-Path $root 'logs/Orders.Service.log') -Tail 30
Get-ChildItem -LiteralPath (Join-Path $root 'warehouse') -Recurse -Filter '*.json'
```

Open RabbitMQ management at `http://127.0.0.1:18901` for the default guide base;
login `playground` / `local-playground-only`, select vhost `$name`. Messages may
already be consumed. Files normally move into `warehouse/processed`; errors go
to `warehouse/errors`. Follow the business mapping from OrderId to WarehouseReference;
do not add order/diagnostic metadata to the warehouse response JSON.

SQLite files are under `.local/<name>/data/<Service>/state.db`. The Inventory
inspection mode above reads with `Mode=ReadOnly`, creates no orders, and never
stops/resets anything. Inspecting another service's DB is a learner/test action,
not an application integration contract. See the [README data table](../README.md#logs-data-and-warehouse-files).

## Explicit stop, restart and cleanup

**Shell: the same PowerShell 7 session. Working directory: the same repository root.**
Stop preserves business data, warehouse files, logs and the broker volume:

```powershell
./scripts/lab.ps1 stop -Name $name -PortBase $portBase
./scripts/lab.ps1 start -Name $name -PortBase $portBase
Invoke-RestMethod "$checkout/$orderId" | ConvertTo-Json -Depth 6
```

The order must still be Completed with its original invoice ID. Log files append
across restarts. If using another terminal, recover the printed name/base/OrderId
first; do not guess an environment or delete its owner/manifest.

**Optional destructive cleanup. Shell: the same PowerShell 7 session. Working
directory: the same repository root. Run only when finished inspecting:**

```powershell
./scripts/lab.ps1 reset -Name $name -PortBase $portBase
```

This stops only the selected lab and removes its data and broker volume. It is
not part of the manual order block. Copy any evidence you need before reset.

## Automated happy path and payment decline

**Shell: zsh or PowerShell 7. Working directory: repository root; Release already built.**

```text
pwsh -NoProfile -File ./scripts/e2e.ps1 -Name explore-auto -PortBase 18500 -NoBuild
```

This separate test creates fresh `explore-auto-1` and `explore-auto-2` environments,
checks normal completion, Inventory independently, and payment decline without
reservation or warehouse dispatch. It reconciles persisted effects and business
files. It **does stop/reset its own two environments**, including on failure, and
preserves reports/logs/data under `.local/e2e-evidence/`. Use another Name if either
namespace already exists. `summary.json` requires two successes plus successful cleanup.
[Detailed E2E guide (Italian)](end-to-end.md).

## Failure and recovery

This repeatable US11 scenario uses all eight real hosts and its own real RabbitMQ
container. It selects `missing-warehouse-response`, observes a persisted shipment
while Orders remains pending without capture, interrupts the simulator, and restarts
it in happy-path mode. The order must then complete with the **original ShipmentId**.
It cleans up only its generated `us11-<UUID>` container, dynamic ports and temporary
directory. It does not require or reset your manual environment.

**Shell: PowerShell 7. Working directory: repository root; Release already built,
Docker Linux engine running.**

<!-- delivery-example:start -->
```powershell
$previousBrokerTests = [Environment]::GetEnvironmentVariable('PLAYGROUND_BROKER_TEST')
try {
    $env:PLAYGROUND_BROKER_TEST = '1'
    dotnet test --no-build -c Release --filter 'FullyQualifiedName~FailureScenarioTests.Missing_response_remains_pending_until_explicit_simulator_recovery'
    if ($LASTEXITCODE -ne 0) { throw 'US11 scenario failed' }
} finally {
    if ($null -eq $previousBrokerTests) { Remove-Item Env:PLAYGROUND_BROKER_TEST -ErrorAction SilentlyContinue }
    else { $env:PLAYGROUND_BROKER_TEST = $previousBrokerTests }
}
```
<!-- delivery-example:end -->

Expected: **1 passed, 0 skipped**. A skipped test is unverified. Other failures,
expected states and precise operator recoveries appear in the
[US11 catalog (Italian)](failure-scenarios.md). The test distinguishes real process
interruptions from in-process file checkpoints; neither proves power-loss durability.
CI executes these two marked examples; **its own wrapper** stops/resets the manual
example's generated lab after collecting output. That CI cleanup is not embedded
in the manual instructions.

## Troubleshooting

Use the [README troubleshooting table](../README.md#troubleshooting) for SDK, Docker,
architecture, ports, permissions and ownership failures. For a pending order inspect
`lastError`, service logs, retry budgets, error queues and warehouse errors before
changing anything. A timeout is not a payment decline. Restarting does not reset an
exhausted retry budget; apply the owning service's targeted recovery while preserving
business IDs. Detailed service guides and historical limitations are linked from
the [repository map](../README.md#repository-and-further-reading).
