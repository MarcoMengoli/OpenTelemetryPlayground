# OpenTelemetryPlayground

Playground ecommerce locale .NET da esplorare **prima di aggiungere OpenTelemetry**.
Il percorso completo è implementato: checkout, autorizzazione, prenotazione Inventory
ed evasione in parallelo, file magazzino, spedizione, addebito e fattura simulata.
Persistenza SQLite per servizio, idempotenza, outbox e retry limitati sono inclusi.
Nessuna UI, instrumentazione applicativa OTel, exporter, Collector o dipendenza Aspire.
I log essenziali usano `ILogger<T>` e Serilog console; la diagnostica interna del
runtime e delle librerie rimane invariata.

Baseline applicativa integrata e verificata:
[`c9f2a065716c657130533225e42ccc0a721454b0`](https://github.com/MarcoMengoli/OpenTelemetryPlayground/commit/c9f2a065716c657130533225e42ccc0a721454b0),
con US10 e US11. [Riferimenti, risultati e limiti della consegna](docs/delivery.md).

## Prerequisiti

- Windows o Linux, .NET SDK **10.0.400** esatto (`global.json`), target `net10.0`.
- PowerShell **7.2+** (`pwsh`), Docker con daemon Linux funzionante e Compose v2.
- Porte loopback libere: base/base+1 per AMQP/management, base+10..17 per gli otto host.
- Accesso a NuGet e Docker Hub per il primo restore/pull; nessun account cloud richiesto.

Gli script di laboratorio supportano Windows/Linux. Build e test offline su macOS
non certificano l'avvio o le prove broker. Versioni in `Directory.Packages.props`,
lock transitive in `packages.lock.json`, immagine `rabbitmq:4.1.4-management`
in `infra/compose.yaml`: non occorre aggiornarle per iniziare.

## Da checkout pulito

Dalla radice del repository, in PowerShell:

```powershell
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet test --no-build -c Release
```

Un test broker **skipped** nel comando offline è non verificato. Per provare subito
normale e rifiuto, due volte con tutti i servizi reali e reset isolato:

```powershell
pwsh ./scripts/e2e.ps1 -Name explore-a -PortBase 18500 -NoBuild
```

Il comando richiede namespace nuovi, controlla Completed/InvoiceId, aspetta Inventory
separatamente, riconcilia magazzino e verifica l'assenza di evasione per il rifiuto.
Conserva report, log, database e file in `.local/e2e-evidence/`. Prerequisiti assenti,
timeout o cleanup fallito producono errore. [Dettagli US10](docs/end-to-end.md).

Per creare e seguire personalmente un ordine via HTTP usare la
[guida copiabile fino alla fattura](docs/getting-started.md). Include un esempio
reale di guasto e ripristino; la CI esegue gli stessi blocchi della guida.

## Avvio, arresto e reset manuali

Scegliere una coppia nome/porte libera, diversa da altri lavori:

```powershell
pwsh ./scripts/lab.ps1 start  -Name explore-manual -PortBase 18900
pwsh ./scripts/lab.ps1 ready  -Name explore-manual -PortBase 18900
pwsh ./scripts/lab.ps1 verify -Name explore-manual -PortBase 18900
pwsh ./scripts/lab.ps1 stop   -Name explore-manual -PortBase 18900
# Riparte conservando dati e volume:
pwsh ./scripts/lab.ps1 start  -Name explore-manual -PortBase 18900
# Rimuove soltanto dati, processi e volume di questo ambiente:
pwsh ./scripts/lab.ps1 reset  -Name explore-manual -PortBase 18900
```

`start` richiede build Release, avvia broker e otto processi e attende readiness.
Readiness attesta infrastruttura disponibile, non un ordine completato. `verify`
abilita tutte le prove RabbitMQ, comprese quelle US11, e i controlli infrastrutturali;
E2E US10 verifica esplicitamente il percorso normale/rifiuto.
[Catalogo US11 con azioni, attese e recupero](docs/failure-scenarios.md).

## Configurazione, dati e diagnosi

`lab.ps1` configura nome, root assoluta, porte e credenziali locali. Per avvio con
debugger: [variabili di esempio](infra/lab.env.example) e [mappa host/codice](docs/flow-map.md).

- Broker: vhost `<Name>`, progetto Compose `otelplay-<Name>-<hash-del-worktree>`, volume proprio.
- Dati: `.local/<Name>/data/<Service>/state.db`, un database per host.
- File: `.local/<Name>/warehouse/{requests,responses,processed,errors}`.
- Log: `.local/<Name>/logs/<Service>.log` e `.err.log`, conservati dopo `stop`.
- `stop` verifica PID e identità di avvio; `reset` controlla owner, porte e link.
  Niente prune globale. Usare la coppia Name/PortBase originale; worktree diversi
  richiedono comunque porte diverse.

Credenziali pubbliche esclusivamente locali: `playground` / `local-playground-only`.
Con base 18900, management è su `http://127.0.0.1:18901`, Checkout su 18910.
Un arresto del terminale può lasciare host attivi: usare `ready`/`stop` con i dati
originali, senza avviare una seconda copia o terminare processi per nome.
[Diagnosi di avvio, profili e ordini pendenti](docs/getting-started.md#diagnosi).

## Approfondimenti

- [Flusso, code, stati e punti del codice](docs/flow-map.md)
- [Contratti v1 ed esempi JSON](docs/contracts/README.md)
- [Payments](docs/payments.md), [Checkout/Orders](docs/checkout-orders.md),
  [completamento](docs/order-completion.md), [Inventory](docs/inventory.md)
- [Fulfillment/Couriers](docs/fulfillment.md), [file magazzino](docs/warehouse-files.md),
  [WarehouseResponse](docs/warehouse-response.md)
- [E2E US10](docs/end-to-end.md), [guasti US11](docs/failure-scenarios.md),
  [baseline e consegna US12](docs/delivery.md)
- [Architettura iniziale US02](docs/architecture/README.md) e
  [verifiche storiche](docs/architecture/verification-us02.md)

Le sezioni US02 che parlano di route 501 o attività future descrivono la base storica;
le guide dei servizi e il codice integrato descrivono il comportamento attuale.
Disponibilità Inventory garantita: la prenotazione non blocca Fulfillment. Pagamenti,
fatture, corrieri e magazzino sono simulati. Singola istanza per servizio, nessuna
transazione distribuita o garanzia di produzione. Retry/duplicati conservano gli ID
business; budget esauriti richiedono recupero operatore esplicito.
