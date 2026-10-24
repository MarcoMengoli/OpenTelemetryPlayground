# OpenTelemetryPlayground — base US02

Playground didattico locale .NET, **senza instrumentazione OpenTelemetry o Aspire**.
La US02 avvia sette host applicativi e Warehouse.Simulator, configura Serilog/ILogger<T>,
SQLite, directory condivise e RabbitMQ. **Il checkout completo non è implementato**.
Payments.Api implementa le [operazioni simulate e persistenti della US03](docs/payments.md).
Gli altri endpoint business rispondono `501 not_implemented`, i worker non consumano ancora
messaggi business e il simulatore non produce ancora spedizioni. La readiness certifica
solo l'infrastruttura di base, non il flusso ecommerce.

## Prerequisiti e versioni

- .NET SDK **10.0.400** esatto (`global.json`), target `net10.0`.
- PowerShell 7.2+ (`pwsh`) per gli script, su Windows/Linux.
- Docker con daemon Linux funzionante e Docker Compose v2.
- Porte libere: base e base+1 per RabbitMQ, base+10..17 per gli host.
- Nessun account cloud. Il primo restore/pull richiede accesso a NuGet/Docker Hub.

.NET 10 è LTS: [support policy ufficiale](https://dotnet.microsoft.com/en-us/platform/support/policy).
Pacchetti con versioni esatte in `Directory.Packages.props` e dipendenze transitive bloccate
nei `packages.lock.json`. L'immagine RabbitMQ è `rabbitmq:4.1.4-management`.
La patch Microsoft.Data.Sqlite 10.0.13 evita la dipendenza nativa vulnerabile trovata con 10.0.0.
Aggiornamenti SDK/pacchetti/immagini devono essere espliciti e rieseguire le verifiche.

## Build e test da checkout pulito

```powershell
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet test --no-build -c Release
```

Il test RabbitMQ è **skipped** nel comando offline: questo non prova il trasferimento reale.
Gli altri test verificano tutti gli esempi pubblicati, payload invalidi, isolamento dei nomi
e persistenza SQLite tra riaperture. I test Payments aggiungono HTTP reale, duplicati
concorrenti, transizioni e recupero dopo riavvio o perdita della risposta.

## Ambiente locale

Eseguire dalla radice del repository, con una coppia nome/porte riservata al proprio lavoro:

```powershell
pwsh ./scripts/lab.ps1 start  -Name us02-b -PortBase 18200
pwsh ./scripts/lab.ps1 ready  -Name us02-b -PortBase 18200
pwsh ./scripts/lab.ps1 verify -Name us02-b -PortBase 18200
pwsh ./scripts/lab.ps1 stop   -Name us02-b -PortBase 18200
# Riparte conservando database e volume RabbitMQ:
pwsh ./scripts/lab.ps1 start  -Name us02-b -PortBase 18200
# Distruttivo SOLO per questo ambiente: ferma, rimuove il suo volume e .local/us02-b:
pwsh ./scripts/lab.ps1 reset  -Name us02-b -PortBase 18200
```

`start` richiede la build Release già eseguita. Avvia Compose, attende la salute del broker,
poi avvia otto host locali e controlla la loro readiness. Fallisce se le porte sono occupate.
`verify` abilita il test RabbitMQ reale: publish con confirm/mandatory, consumo con OrderId,
nack/requeue, redelivery, ack e verifica della coda vuota; verifica anche un messaggio non
instradabile. Usa exchange e coda unici, rimossi alla fine. Verifica inoltre log di avvio,
file SQLite, scrittura/lettura delle quattro directory e risposta 501 del checkout.

| Host | Porta con base 18200 | Base endpoint |
|---|---:|---|
| Checkout.Api | 18210 | `/health/ready` |
| Orders.Service | 18211 | `/health/ready` |
| Payments.Api | 18212 | `/health/ready` |
| Inventory.Service | 18213 | `/health/ready` |
| Fulfillment.Service | 18214 | `/health/ready` |
| Couriers.Api | 18215 | `/health/ready` |
| WarehouseResponse.Service | 18216 | `/health/ready` |
| Warehouse.Simulator | 18217 | `/health/ready` |

RabbitMQ AMQP è su `127.0.0.1:18200`; management su `http://127.0.0.1:18201`.
Credenziali locali pubbliche: `playground` / `local-playground-only`. Porte bindate solo al
loopback: queste credenziali non sono destinate a un deployment condiviso o in produzione.

## Isolamento, dati e log

- Il progetto Compose è `otelplay-<Name>-<hash-del-worktree>`; il volume è scoped al progetto.
- Il vhost è `<Name>`, exchange e code sono prefissati da `<Name>`; i test aggiungono un UUID.
- Database: `.local/<Name>/data/<Service>/state.db`, uno per host.
- File: `.local/<Name>/warehouse/{requests,responses,processed,errors}`.
- Ogni host ha un piccolo processo PowerShell attivo fino alla sua uscita: conserva i log
  anche dopo che `start` termina. Log Serilog console catturati in `.local/<Name>/logs/<Service>.log` e `.err.log`.
- Il manifest `.local/<Name>/processes.json` conserva PID e identità di avvio del sistema operativo. `stop` verifica
  la proprietà del processo e chiede lo shutdown tramite `.local/<Name>/run/<Service>.stop`.
  È un controllo locale del ciclo di vita, separato dai contratti business del magazzino.
- `stop` conserva dati e volume. `reset` verifica ownership e percorsi prima della rimozione;
  non usa prune, non tocca altri progetti Docker e rifiuta link/junction nelle directory da eliminare.
- Worktree distinti devono comunque scegliere **porte diverse**; esempio A: `-Name us01-a -PortBase 18300`.
  Non cambiare la base porte di un ambiente esistente: prima `reset` con la configurazione originale.
- Un arresto del terminale/script può lasciare processi attivi: riprendere con `ready` o `stop`,
  non avviare una seconda copia. Un PID riciclato viene rifiutato, mai terminato alla cieca.

Per un avvio manuale o debugger, impostare le variabili documentate in
[infra/lab.env.example](infra/lab.env.example); `Lab__Root` deve essere un percorso assoluto
comune agli host del solo ambiente scelto. Le variabili corrispondono a `Lab:Name`, `Lab:Root`,
`Lab:RabbitHost`, `Lab:RabbitPort`, `Lab:RabbitUser`, `Lab:RabbitPassword` in IConfiguration.
`ASPNETCORE_URLS` sceglie la porta del singolo host. Lo script imposta questi valori esplicitamente.

## Specifiche e consegna

- [Architettura e confini delle story](docs/architecture/README.md)
- [Contratti HTTP, messaggi e file v1](docs/contracts/README.md)
- [Payments.Api: operazioni, scenari e verifiche US03](docs/payments.md)
- [Esiti e limiti delle verifiche US02](docs/architecture/verification-us02.md)
- [Specifica US02](https://github.com/MarcoMengoli/OpenTelemetryPlayground/issues/2)

Disponibilità inventario garantita nella prima versione: Inventory e Fulfillment procedono in
parallelo. Pagamenti, fatture, corrieri e magazzino sono simulati. Nessuna UI richiesta.
A possiede AGENTS.md, skill, docs/agents, backlog e workflow: questa PR non ne crea copie.
