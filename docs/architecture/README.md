# Architettura della base US02

## Componenti e responsabilità

`OpenTelemetryPlayground.slnx` contiene otto host, `Playground.Contracts`,
`Playground.Infrastructure` e `Playground.Tests`. Gli host usano tutti ASP.NET Core per una
readiness uniforme; i servizi asincroni aggiungeranno BackgroundService nelle proprie story.
Le chiamate HTTP, i consumer e i publisher business saranno espliciti nei rispettivi progetti.
Non c'è un framework di orchestrazione, un bus astratto o un progetto Observability.

| Componente | Proprietà futura | Stato in US02 |
|---|---|---|
| Checkout.Api | HTTP acquisizione; registro richieste e outbox OrderCreated | host, readiness, route 501 |
| Orders.Service | stato ordine; consumer; HTTP Payments; outbox comandi | host, readiness, route interna 501 |
| Payments.Api | autorizzazioni, addebiti, fatture simulate | host, readiness, route 501 |
| Inventory.Service | stock e reserved; inbox | host e readiness |
| Fulfillment.Service | stime corriere, mapping magazzino, file outbox | host, readiness, lookup 501 |
| Couriers.Api | stima deterministica simulata | host, readiness, route 501 |
| WarehouseResponse.Service | inbox file e outbox ShipmentConfirmation | host e readiness |
| Warehouse.Simulator | richieste e risposte simulate idempotenti | host e readiness |

US02 dichiara la topologia RabbitMQ durevole. Non attiva consumer business che farebbero
sparire messaggi non ancora elaborabili. I soli trasferimenti sono quelli delle verifiche su
risorse temporanee isolate. Le directory sono predisposte e testate, non processate.

## Persistenza e punti di estensione

SQLite per servizio, journal WAL, timeout lock 5 secondi. `LocalStore` inizializza soltanto
`infrastructure_probe(id=1, starts)` per verificare scrittura e persistenza tra riavvii.
Non ci sono finte tabelle inbox/outbox inutilizzate o finte garanzie di exactly-once.
Ogni story aggiungerà il proprio schema SQL e transazioni accanto al codice che lo usa:

- Checkout: unicità OrderId e payload originale; inserimento richiesta + OrderCreated outbox nella
  stessa transazione. Stesso OrderId e stesso payload sono un retry, payload diverso è 409.
- Orders: stato ordine + inbox UNIQUE(MessageId) + outbox in un'unica transazione. UNIQUE(OrderId)
  protegge anche duplicati logici con MessageId diversi. Ripartenza riprende lo stato durabile.
- Payments: autorizzazione UNIQUE(OrderId), addebito UNIQUE(AuthorizationId) con CaptureId stabile, fattura UNIQUE(CaptureId) con InvoiceId stabile.
  Chiamate HTTP ripetute restituiscono lo stesso risultato; rifiuto business è anch'esso persistito.
- Inventory: prenotazione UNIQUE(OrderId, Sku), inbox e aggiornamento reserved atomici.
- Fulfillment: mapping UNIQUE(WarehouseReference), UNIQUE(OrderId), stima e record file-outbox
  nella stessa transazione. WarehouseResponse usa l'endpoint interno di lookup, mai il DB altrui.
- Simulator: risultato UNIQUE(WarehouseReference), ShipmentId e ShippedAt salvati prima di pubblicare
  la risposta; ripetizione della richiesta rigenera lo stesso file business.
- WarehouseResponse: inbox UNIQUE(WarehouseReference, ShipmentId), validazione del mapping e outbox
  ShipmentConfirmation atomiche. Riferimento già confermato con ShipmentId diverso è conflitto.

I dispatcher outbox useranno confirm RabbitMQ o rename atomico dei file, marcando l'invio solo
successivamente. Crash tra invio e marcatura causa duplicazione prevista, gestita dai destinatari.
Nessuna transazione distribuita. Errori temporanei HTTP mantengono lo stato riprendibile;
non trasformano timeout in rifiuti di pagamento. Gli ID business sono le chiavi idempotenti.

## Avvio, prontezza e arresto

`FoundationLifecycle` verifica SQLite, lettura/scrittura directory, connessione AMQP e dichiarazione
della topologia prima che Kestrel accetti richieste. ILogger<T> passa a Serilog console per avvio,
arresto e fallimenti. Un broker irraggiungibile causa uscita non-zero e log esplicito; nessuna
readiness positiva fittizia. Durante l'esecuzione readiness controlla connessione aperta e lettura DB.
L'auto-recovery del client può ristabilire la connessione; non garantisce il successo di operazioni
in corso e non sostituisce inbox/outbox. Un host morto prima della readiness va riavviato dopo la
correzione dell'infrastruttura. I file `.stop` sotto `run/` consentono un arresto ordinato locale.

La versione iniziale usa una sola istanza per servizio per ambiente. Il modello di persistenza
supporta retry/duplicati locali; concorrenza multiistanza e alta disponibilità sono fuori scope.
I test della base non dimostrano il flusso completo, che verrà verificato in US10/US11.

## Scenari deterministici futuri

Le story applicative aggiungeranno configurazione locale `Simulation:Profile` (default `happy-path`),
`Simulation:DelayMs`, `Simulation:FailuresBeforeSuccess` e selezione per OrderId o WarehouseReference.
Profili: `payment-declined`, `slow-courier`, `temporary-payment-failure`, `duplicate-warehouse-response`.
Il simulatore riceve la configurazione dal proprio ambiente, non da campi diagnostici nei file.
Per errore N-volte, il contatore tentativi della chiave business deve sopravvivere al riavvio.
Per duplicazione, si riusa lo stesso risultato business. Queste opzioni sono **contratti di sviluppo
futuro**, non interruttori già funzionanti in US02. Non aggiungere trace context/baggage.

## Integrazione con A

Comandi stabili: `dotnet restore --locked-mode`, `dotnet build --no-restore -c Release`,
`dotnet test --no-build -c Release`. Per una verifica completa della base:
`pwsh ./scripts/lab.ps1 start -Name ci-us02 -PortBase 18400`, poi `verify`, poi `reset` in un finally.
La CI deve distinguere il test broker skipped dai test reali eseguiti con `verify`.
SDK in global.json, pacchetti in Directory.Packages.props, immagine in infra/compose.yaml.
Payload/ID/stati sono fissati in docs/contracts/README.md e Playground.Contracts/Contracts.cs.
Qualsiasi modifica successiva richiede indicare impatto su entrambi i percorsi.
Non eseguire US03/US07 dalla presunzione che una PR aperta sia già integrata.

