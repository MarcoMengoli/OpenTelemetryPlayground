# Payments.Api — US03

Pagamenti e fatture sono simulati per il laboratorio locale. Nessun provider,
dato carta o documento fiscale reale. Le route e i DTO restano quelli dei
[contratti v1](contracts/README.md#operazioni-payments-e-recupero-orders-us03-e-us06).

## Operazioni ed effetti

| Operazione | Identità del retry | Effetto persistito |
| --- | --- | --- |
| Autorizzazione | OrderId | Authorized con AuthorizationId oppure Declined senza autorizzazione |
| Annullamento | AuthorizationId | Authorized → Cancelled |
| Addebito (capture) | AuthorizationId | Authorized → Captured con CaptureId |
| Fattura | CaptureId | Captured → Invoiced con InvoiceId |

Ripetere la stessa operazione recupera il risultato originale, anche dopo riavvio
o perdita della risposta. Autorizzare nuovamente un ordine annullato o addebitato
restituisce l'autorizzazione originale senza cambiare lo stato corrente. Capture
resta recuperabile anche dopo la fattura. Annullare dopo capture/fattura o addebitare
dopo annullamento produce 409 invalid_state. Un riferimento inesistente non può
creare un effetto: 409 invalid_state. Riferimento appartenente a un altro OrderId
o importo diverso sulla stessa autorizzazione: 409 idempotency_conflict.

La fattura usa esclusivamente l'importo già persistito. Il rifiuto business è
200 Declined, con declineCode simulated_decline; un errore temporaneo è 503,
senza trasformarsi in un rifiuto. GET /api/v1/payments/{orderId} mostra lo stato
e i tre ID separati; 404 se non esiste ancora un risultato business. JSON invalido,
campi mancanti/aggiuntivi, importo non positivo o con più di due decimali,
valuta diversa da EUR e UUID vuoti sono 400 invalid_request. Gli errori usano ApiError.

## Persistenza e concorrenza

Lo schema è nel servizio, in PaymentStore.Initialize. Solo il database
`.local/<Name>/data/Payments.Api/state.db` viene usato. La tabella payments conserva
un risultato per OrderId, con indici UNIQUE su AuthorizationId, CaptureId e InvoiceId.
Una transazione SQLite IMMEDIATE comprende lettura, verifica dello stato e scrittura:
due richieste concorrenti non possono entrambe creare una nuova identità o vincere
la gara tra annullamento e capture. Gli importi sono decimali in testo invariant,
senza conversione floating point. La scala 12.5/12.50 non crea un conflitto.

payment_attempts conserva la richiesta e il contatore per (operation, operation_key).
Anche un'autorizzazione fallita temporaneamente vincola la chiave all'importo iniziale.
Il contatore viene committato prima del 503; riavviare non rinnova il budget simulato.
Replay già completati recuperano subito il risultato senza nuovi tentativi/effetti.
Nessun retry automatico sul server; il chiamante applica i limiti del contratto.
SQLite occupato restituisce 503; altri guasti non previsti sono loggati e diventano
500 internal_error senza dettagli interni nella risposta.

## Scenari deterministici

Configurazione ASP.NET standard, passata al solo processo Payments.Api tramite
argomenti `--Simulation:...` oppure variabili `Simulation__...`:

| Chiave | Default | Comportamento |
| --- | --- | --- |
| Simulation:Profile | happy-path | happy-path, payment-declined, temporary-payment-failure |
| Simulation:OrderId | assente | UUID selezionato; assente seleziona tutti gli ordini |
| Simulation:DelayMs | 0 | 0–60000 ms prima della risposta di ogni operazione riuscita/replay selezionato |
| Simulation:FailuresBeforeSuccess | 0 | 0–1000 primi tentativi falliscono, per ciascuna operazione e chiave selezionata, nel profilo temporary-payment-failure |

Esempio di argomenti da aggiungere all'avvio del servizio, mantenendo la configurazione
Lab e le risorse isolate del [README](../README.md):

```text
--Simulation:Profile temporary-payment-failure --Simulation:FailuresBeforeSuccess 2
--Simulation:OrderId 11111111-1111-4111-8111-111111111111
```

Per il rifiuto usare payment-declined. Per un esito HTTP ambiguo usare happy-path con
DelayMs maggiore del timeout del chiamante: la transazione viene committata **prima**
del ritardo. Una disconnessione durante il ritardo lascia il risultato recuperabile
con la stessa chiave. Il ritardo non mantiene aperta la transazione e non rallenta GET.
Anche un replay può essere ritardato: ridurre DelayMs per osservare il recupero senza
attendere. Cambiare profilo non modifica risultati già persistiti. Per ricominciare
usare un nuovo OrderId o il reset del solo ambiente isolato. Non cambiare i payload
per trasportare la configurazione; nessun metadato diagnostico viene aggiunto.

## Verifiche

```text
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet test --no-build -c Release --filter FullyQualifiedName~PaymentTests
dotnet test --no-build -c Release
```

PaymentTests usa Kestrel reale su porta assegnata dal sistema, gli stessi handler e
il medesimo store SQLite del servizio, con una directory UUID per test. Verifica
operazioni concorrenti, conflitti, stati, rifiuti e selezione per ordine, budget di
errore persistenti e perdita della risposta **dopo l'effetto** per tutte e quattro
le operazioni. Ogni prova di recupero arresta il server HTTP e lo ricrea sullo stesso DB.
I test non sostituiscono la connessione RabbitMQ di FoundationLifecycle con un mock:
esercitano gli handler senza quel lifecycle. Il processo reale continua a richiedere
il broker e la readiness della base. La CI esistente verifica separatamente gli otto
host, SQLite/directory/log, RabbitMQ reale, stop e reset tramite scripts/lab.ps1.
Questa story non verifica ancora il flusso Orders o l'intero ecommerce.
