# US10 — verifica ecommerce completa

Da una base integrata con US06 e US09, eseguire su Windows/Linux con SDK fissato,
PowerShell 7.2+ e Docker Linux funzionante:

```powershell
pwsh ./scripts/e2e.ps1 -Name us10-a -PortBase 18500
```

Il comando esegue restore locked e build Release, quindi **due esecuzioni reali**
nei namespace `us10-a-1` e `us10-a-2`, con reset tra le prove. `-NoBuild` è per una
Release già compilata (come in CI); binari mancanti causano errore. Mancanza di
Docker/PowerShell, porte occupate o prerequisiti mancanti non producono skip o
successo. La piattaforma macOS non è supportata dallo script di laboratorio US02:
la compilazione/verifica offline su Mac non certifica questo percorso.

Il ciclo di vita resta quello US02: scripts/e2e.ps1 chiama start/ready/stop/reset di
scripts/lab.ps1. Non introduce un altro orchestratore o un bus di test. Si avviano
gli otto processi applicativi reali, ciascuno con SQLite proprio, RabbitMQ reale e
le directory condivise del solo ambiente scelto. Nessun host, provider simulato
del laboratorio o tratto del percorso è sostituito da un test double.

## Scenari e osservazione

Un piccolo verifier console, `tests/Playground.EndToEnd`, usa i contratti integrati
e HTTP pubblico; non avvia host in-process e non produce messaggi/file per saltare
passi applicativi. I database sono aperti esclusivamente in modalità **ReadOnly**
come testimoni degli effetti, senza creare schema, acquisire ordini o scrivere dati.
Il nuovo progetto non aggiunge pacchetti/versioni e viene compilato dalla soluzione.

Ogni esecuzione controlla prima readiness di tutti gli host e assenza di dati business
preesistenti. Genera due OrderId e invia checkout v1, con retry identici della POST:

- percorso normale fino a Completed, con AuthorizationId, ShipmentId, CaptureId e
  InvoiceId coerenti con i servizi reali e un solo effetto per operazione;
- pagamento rifiutato fino a PaymentDeclined, senza capture/fattura, prenotazioni,
  stima corriere, lavoro Fulfillment o pubblicazioni di evasione per quell'ordine.

Gli stati effettivamente osservati via HTTP sono registrati: un polling può non
vedere stati intermedi molto rapidi e non ne dichiara l'osservazione.
Il verifier aspetta **separatamente** Inventory dopo Completed: quantità per riga/SKU
e aggregati reserved devono corrispondere a BOOK=2 e PEN=3, senza righe aggiuntive.
Inventory non diventa una dipendenza che blocca Fulfillment.

Il mapping Fulfillment viene riletto dalla route interna reale. Richiesta magazzino,
stima Couriers, risultato persistito del simulatore, risposta letta da WarehouseResponse,
ShipmentConfirmation e snapshot finale devono riconciliarsi. I file archiviati in
processed/requests e processed/responses vengono validati con WireJson; copie di
trasporto equivalenti sono ammesse, effetti/spedizioni diversi no. Non si aggiungono
campi diagnostici ai file o alle mappe business.

Infine si aspettano outbox inviate, input file elaborati e code drenate. Le statistiche
management devono essere realmente presenti e mostrare gli ack attesi: uno zero
iniziale prima del campionamento non certifica il flusso. Code errori e quarantene
magazzino devono essere vuote. Dopo questa barriera si verifica anche l'assenza di
effetti del rifiuto e l'unicità dei risultati globali nel namespace pulito.

Le attese fanno polling con deadline complessiva di 180 secondi e timeout HTTP 5s,
senza una pausa fissa usata come prova del successo. Errori o timeout riportano la
fase incompleta, gli ultimi snapshot e l'eccezione; lo script conserva evidenze.

## Selezione del rifiuto e compatibilità della base

L'unica estensione di lab.ps1 è opzionale:

```powershell
pwsh ./scripts/lab.ps1 start -Name esempio -PortBase 18500 -DeclinedOrderId <UUID>
```

Il profilo Payments già esistente `payment-declined` e il selettore OrderId vengono
passati al solo processo Payments durante la sua creazione, poi l'ambiente del padre
viene ripristinato. Nessun nuovo profilo o modifica applicativa. Gli altri ordini sono
autorizzati normalmente; gli altri host non ricevono questo profilo. L'opzione richiede
start e UUID non vuoto. I comandi US02 senza l'opzione restano invariati.

e2e.ps1 imposta per i propri figli profili happy-path, ritardi/fallimenti zero e URL
dei servizi del proprio PortBase, evitando selettori o URL ereditati da altri lavori;
conserva e ripristina l'ambiente della shell. I processi già attivi mantengono il loro
ambiente e non sono toccati.

## Isolamento e diagnostica

Risorse manuali A: nomi us10-a-1/us10-a-2, broker 18500/18501, host 18510–18517.
US11 di B usa un altro ambiente (dichiarato us11-b/18800), con altri nomi/porte/dati.
Ogni run ha root `.local/<nome>`, vhost `<nome>` e progetto Compose derivato da nome
e worktree. Lo script rifiuta namespace già esistenti: non resetta dati preesistenti
per ottenere una prova "pulita". Il reset riguarda soltanto gli ambienti creati dalla
propria invocazione e riusa i controlli US02 su owner, porte, PID e link.

Evidenze in `.local/e2e-evidence/<Name>-<UUID>/`, fuori dalle root da resettare:

- run-1/run-2: result.json con esito/fase/identità/snapshot/stati osservati, console
  verification.log, log degli otto host e copie di database/file dopo l'arresto;
- summary.json solo dopo due successi indipendenti e cleanup riuscito.

Anche dopo un errore si tenta stop, raccolta prove e reset; un errore di cleanup non
viene mascherato da successo. Le evidenze non vengono cancellate dal reset del run.
I nomi degli ambienti e gli OrderId devono differire nelle due prove. Un'ulteriore
invocazione con lo stesso Name è possibile se il reset è riuscito: genera nuove
identità ed evidenze, senza sovrascrivere quelle precedenti.

La CI usa `us10-<runId>-<attempt>-1/2` su un runner proprio. Dopo i controlli US02 e
di percorso esegue lo stesso comando con -NoBuild e carica l'artifact
`ecommerce-<runId>-<attempt>` anche in caso di errore. Le verifiche precedenti non
sono rimosse; dotnet test continua a certificare le proprie prove, non il nuovo E2E.
Risultati, SHA, CI e review sono registrati nella issue/PR US10. US11 mantiene i propri
guasti separati; US12 aspetta entrambe le verifiche integrate e identificherà la baseline.
Nessuna instrumentazione OpenTelemetry, exporter, Collector o dipendenza Aspire.
