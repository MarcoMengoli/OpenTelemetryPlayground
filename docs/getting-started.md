# Dal checkout alla fattura

Usare Windows/Linux, PowerShell 7.2+, Docker Linux/Compose e lo SDK esatto del
[README](../README.md). Dalla radice di un checkout pulito:

```powershell
dotnet restore --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Restore non riuscito' }
dotnet build --no-restore -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build non riuscita' }
dotnet test --no-build -c Release
if ($LASTEXITCODE -ne 0) { throw 'Test offline non riusciti' }
```

I test broker offline sono skipped: non dimostrano US10/US11. Il blocco seguente
avvia il laboratorio, esegue vere richieste HTTP, attende la fattura, mostra file
e log e infine ferma/resetta **soltanto il proprio ambiente**. Il nome è sempre
nuovo; base 18900 e porte 18901/18910–18917 devono essere libere. Per altri lavori
cambiare `$portBase` prima dell'esecuzione. Usare una shell dedicata: il blocco
configura i propri processi con profilo happy-path e URL coerenti.

## Un ordine reale e un retry identico

Copiare tutto il blocco in PowerShell. Le due POST hanno lo stesso OrderId e corpo;
la seconda recupera l'accettazione senza ripetere gli effetti. Non chiamare
manualmente capture/invoice: deve farlo Orders dopo la spedizione.

<!-- delivery-example:start -->
```powershell
$ErrorActionPreference = 'Stop'
$name = 'guide-' + [guid]::NewGuid().ToString('N').Substring(0,10)
$portBase = 18900
$root = Join-Path (Get-Location) ".local/$name"
if (Test-Path -LiteralPath $root) { throw 'Il walkthrough richiede un ambiente nuovo' }
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
try {
    ./scripts/lab.ps1 start -Name $name -PortBase $portBase
    ./scripts/lab.ps1 ready -Name $name -PortBase $portBase
    $accepted = Invoke-WebRequest $checkout -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 5
    if ($accepted.StatusCode -ne 202 -or ($accepted.Content | ConvertFrom-Json).orderId -ne $orderId) { throw 'Accettazione inattesa' }
    $deadline = [DateTime]::UtcNow.AddSeconds(180)
    do {
        $snapshot = Invoke-RestMethod "$checkout/$orderId" -TimeoutSec 5
        Write-Host "Ordine $orderId : $($snapshot.status)"
        if ($snapshot.orderId -ne $orderId -or $snapshot.status -eq 'PaymentDeclined') { throw 'Ordine/esito inatteso' }
        if ($snapshot.status -eq 'Completed') { break }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Deadline: ordine non completato' }
        Start-Sleep -Milliseconds 200
    } while ($true)
    if (!$snapshot.authorizationId -or !$snapshot.shipmentId -or !$snapshot.captureId -or !$snapshot.invoiceId -or $snapshot.lastError) { throw 'Completamento incompleto' }
    $payment = Invoke-RestMethod "http://127.0.0.1:$($portBase+12)/api/v1/payments/$orderId" -TimeoutSec 5
    if ($payment.status -ne 'Invoiced' -or $payment.orderId -ne $orderId -or
        $payment.authorizationId -ne $snapshot.authorizationId -or $payment.captureId -ne $snapshot.captureId -or
        $payment.invoiceId -ne $snapshot.invoiceId -or $payment.total.amount -ne 29.50) { throw 'Pagamento non riconciliato' }
    $retry = Invoke-WebRequest $checkout -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 5
    $again = Invoke-RestMethod "$checkout/$orderId" -TimeoutSec 5
    if ($retry.StatusCode -ne 202 -or $again.status -ne 'Completed' -or $again.invoiceId -ne $snapshot.invoiceId) { throw 'Retry non idempotente' }
    $snapshot | ConvertTo-Json -Depth 6 | Write-Host
    Get-ChildItem -LiteralPath (Join-Path $root 'warehouse/processed') -Recurse -Filter '*.json' |
        ForEach-Object { Write-Host $_.FullName; Get-Content -LiteralPath $_.FullName | Write-Host }
    Get-ChildItem -LiteralPath (Join-Path $root 'logs') -Filter '*.log' |
        Select-String -SimpleMatch $orderId | ForEach-Object { Write-Host $_.Line }
    Write-Host "WALKTHROUGH OK: $orderId, fattura $($snapshot.invoiceId), ambiente $name"
} catch {
    $snapshot | ConvertTo-Json -Depth 6 | Write-Host
    if (Test-Path -LiteralPath (Join-Path $root 'logs')) {
        Get-ChildItem -LiteralPath (Join-Path $root 'logs') -Filter '*.log' |
            ForEach-Object { Write-Host $_.Name; Get-Content -LiteralPath $_.FullName -Tail 30 | Write-Host }
    }
    throw
} finally {
    if (Test-Path -LiteralPath (Join-Path $root 'owner.json')) {
        try { ./scripts/lab.ps1 stop -Name $name -PortBase $portBase }
        finally { ./scripts/lab.ps1 reset -Name $name -PortBase $portBase }
    }
}
```
<!-- delivery-example:end -->

Sono attesi `202` e Location `/api/v1/orders/<OrderId>` dalla POST, poi uno snapshot
`Completed` con AuthorizationId, ShipmentId, CaptureId e InvoiceId; Payments è
`Invoiced`, importo 29.50 EUR. Il polling può non osservare gli stati intermedi rapidi.
Un 202 dopo il retry rappresenta l'accettazione originale, non il nuovo stato.
Per consultare un ambiente lasciato acceso, la GET copiabile è:

```powershell
Invoke-RestMethod "http://127.0.0.1:18910/api/v1/orders/$orderId"
```

Questa GET richiede l'ambiente ancora attivo; il walkthrough lo resetta alla fine.
Per esplorarlo a mano, seguire il ciclo manuale del README con un altro nome libero.
Prima del reset copiare i log/file utili; `stop` li conserva, `reset` li rimuove.
Per associare una risposta file all'ordine usare WarehouseReference nella GET interna
Fulfillment, non aggiungere OrderId o metadati diagnostici al file.

## Verifica normale/rifiuto e Inventory

Il completamento Orders non è una barriera per Inventory, che procede in parallelo.
La suite seguente aspetta separatamente le prenotazioni BOOK=2 e PEN=3, controlla un
solo effetto per operazione, riconcilia mapping/file e code e include il rifiuto del
pagamento senza prenotazione o invio al magazzino. Esegue due ambienti indipendenti:

```powershell
pwsh ./scripts/e2e.ps1 -Name explore-a -PortBase 18500 -NoBuild
```

Report e copie dei dati restano in `.local/e2e-evidence/`; summary.json compare solo
dopo due successi e cleanup riuscito. [Dettagli e deadline US10](end-to-end.md).

## Ripetere un guasto con recupero

Questo test US11 usa tutti gli otto processi reali: persiste una spedizione con
risposta mancante, osserva l'ordine pendente senza capture, termina il simulatore e
lo riavvia happy-path; verifica Completed con lo ShipmentId originale. Crea e rimuove
il proprio container `us11-UUID`, porte e directory, senza un laboratorio manuale
acceso. Richiede Docker funzionante; un test skipped significa non verificato.

<!-- delivery-example:start -->
```powershell
$previousBrokerTests = [Environment]::GetEnvironmentVariable('PLAYGROUND_BROKER_TEST')
try {
    $env:PLAYGROUND_BROKER_TEST = '1'
    dotnet test --no-build -c Release --filter 'FullyQualifiedName~FailureScenarioTests.Missing_response_remains_pending_until_explicit_simulator_recovery'
    if ($LASTEXITCODE -ne 0) { throw 'Scenario US11 non superato' }
} finally {
    if ($null -eq $previousBrokerTests) { Remove-Item Env:PLAYGROUND_BROKER_TEST -ErrorAction SilentlyContinue }
    else { $env:PLAYGROUND_BROKER_TEST = $previousBrokerTests }
}
```
<!-- delivery-example:end -->

Esito atteso: **1 passato, 0 skipped**. Gli altri scenari (duplicati, quarantena,
timeout, kill Orders e broker spento) e i recuperi mirati sono nel
[catalogo US11](failure-scenarios.md). I kill reali sono distinti dalle interruzioni
ai checkpoint in-process; nessuna garanzia di power loss viene dedotta dalle prove.
La CI esegue direttamente i due blocchi marcati della guida oltre alle suite complete.

## Diagnosi

| Sintomo | Controllo e ripresa |
| --- | --- |
| SDK non trovato, restore o Docker falliti | Verificare SDK10.0.400, rete iniziale, docker info e docker compose version; l'esecuzione resta non verificata. |
| Porta occupata o owner diverso | Scegliere nome/intervallo liberi; su dati esistenti usare la coppia originale per stop/reset, senza fermare l'altro lavoro. |
| Host non pronto | Leggere `.local/<Name>/logs/<Service>.err.log` e `.log`. Readiness richiede broker/DB disponibili, non certifica un ordine. |
| Profilo simulazione invalido | Assente è diverso da stringa vuota. Rimuovere le variabili Simulation__OrderId/Simulation__WarehouseReference quando non selezionate, evitare profili Payments sugli altri host. È il problema PowerShell corretto in US10 #23. |
| 400/409 | Rispettare JSON v1, campi esatti e UUID non vuoti; OrderId riusato con contenuto diverso è conflitto. Non cambiare chiave per aggirare un risultato incerto. |
| Ordine pendente o 503 | Leggere LastError, log, outbox/DLQ e file. Timeout non significa rifiuto business. Usare il recupero del servizio proprietario dopo avere corretto la causa. |
| Retry esauriti | Riavviare non azzera il budget. Fermare il servizio interessato e applicare il recupero mirato documentato, conservando ID e prove in DLQ/quarantena. |
| Stop/reset rifiutato | Verificare owner/PID/configurazione; i controlli proteggono processi riciclati e dati esterni. Ispezionare senza prune o kill globale. |

Nei log seguire OrderId fino a Fulfillment, poi il mapping WarehouseReference e
ShipmentId/CaptureId/InvoiceId. I soli log non provano l'unicità: le suite verificano
anche gli effetti persistiti. [Mappa del codice](flow-map.md), [baseline e recuperi](delivery.md).
