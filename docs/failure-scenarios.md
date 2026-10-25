# Catalogo eseguibile dei guasti — US11

Questa suite verifica il playground didattico locale dopo US06 + US09 integrate.
Non aggiunge instrumentazione, dipendenze o contratti. I risultati per SHA e i link
CI/review sono nella [issue US11](https://github.com/MarcoMengoli/OpenTelemetryPlayground/issues/11)
e nella relativa PR; un test skipped non è una verifica riuscita.

## Comandi e prerequisiti

Servono SDK di global.json, Docker Linux raggiungibile e PowerShell 7.2+ per i
comandi del laboratorio. Dalla radice della soluzione:

```powershell
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet test --no-build -c Release
./scripts/lab.ps1 start  -Name us11-b -PortBase 18800
./scripts/lab.ps1 ready  -Name us11-b -PortBase 18800
./scripts/lab.ps1 verify -Name us11-b -PortBase 18800
./scripts/lab.ps1 stop   -Name us11-b -PortBase 18800
./scripts/lab.ps1 reset  -Name us11-b -PortBase 18800
```

Verificare che Name/PortBase siano liberi; stop/reset sempre sulla stessa coppia.
`verify` abilita tutte le prove broker, inclusi gli otto scenari sotto. Per
eseguire soltanto US11, dopo build Release e con Docker disponibile:

```powershell
$env:PLAYGROUND_BROKER_TEST = '1'
try {
    dotnet test --no-build -c Release --filter 'FullyQualifiedName~FailureScenarioTests'
    if ($LASTEXITCODE -ne 0) { throw 'US11 non superata' }
} finally {
    Remove-Item Env:PLAYGROUND_BROKER_TEST
}
```

La suite US11 crea il proprio broker: per questo filtro non serve un laboratorio
già acceso. Gli altri test broker del catalogo sottostante richiedono invece
le impostazioni/istanza predisposte da `lab.ps1 verify`. La CI esegue la suite
completa; non serve modificare il workflow. Per un singolo caso sostituire il
filtro con `FullyQualifiedName~<nome del test>` dalla tabella.

## Otto scenari con tutti i servizi reali

Ogni scenario crea un ambiente nuovo e passa da Checkout a Orders, Payments,
Inventory, Fulfillment, Couriers, Warehouse.Simulator e WarehouseResponse.
Sono otto processi dotnet distinti che eseguono gli assembly Release normali.
Le uniche sostituzioni sono adattatori trasparenti nei due punti HTTP di arresto
esplicitati; il risultato business viene sempre dal servizio reale.
Le query SQL sono osservazioni del test o interventi operatore mirati, non accessi
tra servizi a database altrui. Gli identificativi UUID isolano le risorse; gli esiti
dei guasti non dipendono da casualità.

| Test (filtro per nome) | Setup e azione deterministica | Effetti attesi e ripristino |
| --- | --- | --- |
| Duplicates_quarantine_and_reader_restart_complete_one_business_process | Fermare il lettore prima del checkout; duplicare ReserveInventory e FulfillOrder, anche con nuovo MessageId ma stessi dati; duplicare richiesta/risposta file con nomi diversi; aggiungere JSON malformato e riferimento ignoto; ripartire; duplicare ShipmentConfirmation | Risposta attende senza addebito; quarantena distinta per invalido e ignoto (3 lookup); una prenotazione da 2 BOOK, una spedizione, un addebito e una fattura. Copie AMQP osservate nelle inbox. I file conservano solo i quattro campi business concordati. |
| Missing_response_remains_pending_until_explicit_simulator_recovery | Profilo missing-warehouse-response; attendere la spedizione persistita ma non pubblicata; osservare per 3 s; kill del simulatore e restart happy-path | FulfillmentPending, nessuna capture/outbox conferma durante la finestra. Ripresa con ShipmentId originale fino a Completed. Nessuna cancellazione automatica inventata. |
| Temporary_payment_and_courier_errors_recover_with_persisted_attempts | Profili temporary-payment-failure e temporary-courier-failure, primi 2 tentativi falliti | Tre tentativi persistiti per autorizzazione/capture/fattura e stima; poi Completed con effetti singoli. Payments fallisce prima dell'effetto; Couriers conserva già la stima deterministica. |
| Slow_payment_exhaustion_preserves_remote_authorization_and_recovers_explicitly | Payments ritarda la risposta 6 s dopo l'effetto, timeout Orders 5 s; attendere DLQ; kill/restart consumer senza reset, poi fermare consumer/server, togliere ritardo e azzerare il solo budget dell'ordine | Orders Created con payment_retry_exhausted, Payments già Authorized, nessun addebito. Riavvio solo non riapre il budget. Recupero esplicito mantiene AuthorizationId, arriva a Completed; originale resta in DLQ. |
| Slow_courier_exhaustion_preserves_estimate_and_recovers_explicitly | Couriers lento 6 s, timeout Fulfillment 5 s; stessa procedura di esaurimento/restart/riparazione | Fulfillment Failed con 3 tentativi e courier_timeout, ordine FulfillmentPending, stima remota già persistita. Ripresa conserva stima/riferimento e completa una sola volta; originale resta in DLQ. |
| Kill_orders_after_remote_capture_before_local_commit_recovers_same_capture | Adattatore inoltra la vera capture a Payments e trattiene la risposta; barriera osservabile, kill del processo Orders | Payments Captured, Orders Shipped, inbox non completata, nessuna fattura. Restart Orders direttamente verso Payments recupera lo stesso CaptureId e completa la fattura. |
| Kill_orders_after_capture_commit_before_invoice_recovers_remaining_work | Adattatore lascia passare capture e trattiene invoice prima dell'inoltro; kill Orders alla barriera | Orders e Payments Captured, nessuna fattura. Restart completa il passo rimanente, preservando CaptureId e una sola fattura. |
| Broker_stop_keeps_confirmation_durable_and_full_lab_restart_recovers_it | Simulatore conserva una risposta non pubblicata; stop del solo container privato; il test rilascia quel preciso JSON persistito; attendere outbox esaurita; arrestare gli host, riavviare broker, resettare la sola riga outbox e riavviare gli otto host | Nessuna capture mentre AMQP è assente; outbox sent=0, attempts=3, publish_retry_exhausted. Ripresa con identici MessageId/body e una sola spedizione/capture/fattura. Porta Docker riletta dopo restart. |

Ogni scenario usa attese su stato persistente/endpoint, non conteggi dei log.
Readiness: 30 s per host; avvio broker: 90 s; comando Docker: 60 s; osservazione
DB/file: 60 s (salvo override esplicito); Completed: 90 s; DLQ: 45 s; barriera
capture/invoice: 60 s. Le operazioni HTTP del fixture hanno timeout 5 s,
publish 10 s. Un'attesa può terminare dopo la deadline nominale soltanto per
concludere l'operazione I/O già in corso, anch'essa limitata. Nessun ciclo infinito.
Arresto host: 15 s via .stop, poi kill del solo processo posseduto e attesa 10 s;
i due scenari capture e quello missing eseguono direttamente il kill dichiarato.

## Confini fini già integrati, riusati nella suite completa

Non si raddoppiano i test che esercitano già la stessa finestra. Queste classi
sono eseguibili con lo stesso filtro per FullyQualifiedName, e girano in verify:

| Classe / metodo significativo | Punto di arresto e osservazione |
| --- | --- |
| InventoryBrokerTests.Stop_before_commit_preserves_the_attempt_budget_and_restart_resumes_the_same_reservation | Tentativo persistito prima di commit, nessuna quantità parziale; recupero dopo stop dell'host. |
| InventoryBrokerTests.Stop_after_commit_before_ack_redelivers_but_never_reserves_twice | Broker riconsegna dopo commit prima di ack; reserved non cresce due volte. |
| FulfillmentBrokerTests.Consumer_restarts_before_http_after_http_and_after_commit_before_ack_keep_one_outbox | Conservazione della chiave e di una sola file-outbox nei confini HTTP/commit/ack. |
| OrderBrokerTests.Mandatory_return_and_interruption_between_publishes_preserve_each_outbox_identity | Return mandatory lascia outbox; interruzione fra publish conserva entrambi i comandi con identità originali. |
| WarehouseFileTests.Request_writer_recovers_flush_or_rename_before_database_commit | Eccezione al checkpoint dopo flush/rename; riapertura store/worker, nessun file parziale elaborato. |
| WarehouseFileTests.Simulator_recovers_after_durable_acceptance_or_input_archive | Risposta recuperabile da DB prima/dopo archivio input. |
| WarehouseFileTests.Response_publication_recovers_with_identical_business_identity | Flush/rename prima del mark-sent: identico ShipmentId/ShippedAt su replay. |
| WarehouseResponseBrokerTests.Real_broker_confirm_crash_redelivery_and_stopped_broker_recovery_preserve_identity | Conferma broker prima di sent/archive, riconsegna stessa identità; stop broker reale e budget durabile. |
| CompletionBrokerTests.Real_five_second_capture_timeouts_exhaust_dead_letter_and_recover_explicitly | Capture remota riuscita, risposta persa, budget esaurito, stessa capture al recupero. |

I checkpoint file e publish sono interruzioni deterministiche tra chiamate con
riapertura dei componenti: **non** kill di processo o simulazioni di perdita di
alimentazione. US11 aggiunge i veri kill elencati nella prima tabella. Questa
distinzione deve restare nella consegna US12.

## Isolamento, diagnosi e riprocessamento

Ogni scenario usa container `us11-<uuid>`, vhost omonimo, porta AMQP loopback
dinamica, directory temporanea `playground-us11-<uuid>` e DB separato per servizio.
Le porte HTTP sono riservate su loopback prima dell'avvio e rilasciate una per
volta al processo corrispondente; un conflitto successivo fallisce la readiness,
non termina altri processi. Nessuna porta management pubblicata. La collezione
US11 è seriale, per non contendere risorse CPU con gli altri test del runner.

In finally il fixture ferma solo i processi conservati nei propri handle, rimuove
solo il container dal nome UUID che ha creato e i suoi volumi, quindi la propria
directory. Niente prune, stop globale, ricerca/kill per nome o DB condivisi.
Un errore di cleanup fallisce il test e conserva la directory; segnala eventuali
risorse residue. Le ultime righe stdout/stderr degli host vengono allegate all'output
xUnit per diagnosi dei fallimenti. Repetizioni partono da nuove risorse; i retry
all'interno di uno scenario preservano invece le identità.

Per l'esercizio manuale, dopo aver corretto la causa e fermato il solo servizio
interessato, usare le query mirate documentate in
[Orders](checkout-orders.md), [completamento](order-completion.md),
[Fulfillment](fulfillment.md), [Inventory](inventory.md),
[file](warehouse-files.md) e [WarehouseResponse](warehouse-response.md).
I test eseguono la stessa politica: nessun azzeramento automatico al restart,
nessun nuovo ID per aggirare idempotenza, nessun consumo automatico dalla DLQ.
Il test legge e rimette l'originale nella propria DLQ per verificarne l'identità.
Quarantena e errori persistenti sono prove storiche; non vanno cancellati per
far apparire completato un ordine ancora pendente.

US10 resta la verifica normale/rifiuto di A. US11 riguarda guasti del sistema
integrato; US12 deve riportare entrambi gli esiti sul commit realmente consegnato.
