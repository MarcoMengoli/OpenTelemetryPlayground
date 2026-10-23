# Verifiche US02 — 23 ottobre 2026

Ambiente: Windows, SDK .NET 10.0.400, worktree `us02-ecommerce-foundation`,
branch `codex/us02-ecommerce-foundation`. Nome laboratorio `us02-b`, porte 18200/18201 e 18210–18217.

| Verifica | Comando / evidenza | Risultato |
|---|---|---|
| Restore ripetibile | `dotnet restore --locked-mode` | riuscito |
| Intera soluzione (8 host, 2 librerie, test) | `dotnet build --no-restore -c Release` | riuscito, 0 warning/errori |
| DTO/esempi e persistenza reale SQLite | `dotnet test --no-build -c Release` | 6 test passati, 1 broker skipped |
| Sintassi PowerShell | Parser.ParseFile su scripts/lab.ps1 | riuscita |
| Configurazione Compose | `docker compose -p us02-b-config-check -f infra/compose.yaml config --quiet` con variabili LAB_* | riuscita |
| Avvio Compose | `pwsh ./scripts/lab.ps1 start -Name us02-b -PortBase 18200` | bloccato: daemon Docker assente |
| Avvio host | `dotnet src/<Host>/bin/Release/net10.0/<Host>.dll` per tutti gli 8 host | tentato; fallisce per broker assente, non pronto |
| ILogger/Serilog errori | log di ogni avvio, servizio e livello ERR, Infrastructure startup failed | verificato sui percorsi di errore |
| Directory condivise | bootstrap crea e prova requests/responses/processed/errors prima della connessione broker | verificato nei tentativi host |
| Trasferimento RabbitMQ reale | test BrokerTests predisposto; richiede `lab.ps1 verify` | NON verificato localmente: daemon indisponibile |
| Readiness 200, shutdown ordinato, restart/reset con broker | `start/ready/verify/stop/start/reset` | NON verificati localmente: stesso prerequisito assente |

Docker CLI 20.10.17, Compose 2.6.1. Docker Desktop avviato, ma sia il contesto default sia
desktop-linux non espongono la named pipe del daemon. Anche `wsl -d docker-desktop -- echo ready`
fallisce con distribuzione non disponibile. Nessun reset di Docker/WSL, cancellazione di altri
volumi o aggiornamento del sistema è stato tentato. La correzione dell'ambiente resta separata
ai file del laboratorio. Non sostituiamo il broker con un mock né contiamo uno skip come successo.

Il test broker, quando abilitato tramite PLAYGROUND_BROKER_TEST=1 da `verify`, fallisce se non
riesce a connettersi: non viene saltato silenziosamente. Risorse test con UUID, vhost e porte
espliciti. La verifica completa può essere eseguita dalla CI Ubuntu di A sulla base comune;
esiti e SHA devono essere riportati senza confonderli con esecuzioni locali.

Il primo restore con Microsoft.Data.Sqlite 10.0.0 ha fallito per NU1903 su SQLitePCLRaw.lib.e_sqlite3.
La versione fissata 10.0.13 risolve il problema; non è stato disattivato NuGet Audit.

Review Codex GitHub: ancora da richiedere alla pubblicazione della PR. Richiesta, presa in carico
e completamento saranno registrati nella PR; nessun merge o chiusura issue è autorizzato.

## Prima esecuzione integrata su Ubuntu

A ha eseguito US01+US02 sul commit di integrazione `f233274` (US02 `5c50a74`):
[run 39905289531](https://github.com/MarcoMengoli/OpenTelemetryPlayground/actions/runs/39905289531).
Restore, guard architetturale (anche dipendenze transitive), build, avvio e readiness degli **otto
host** sono riusciti. Il test con broker RabbitMQ reale ha concluso **7 passed, 0 skipped**:
confirm, mandatory return, business OrderId, redelivery e acknowledgement effettivi.

La run complessiva è fallita dopo il test broker: PowerShell Linux richiede `-Force` per eliminare
il file probe nascosto; il confronto DateTime StartTime dei PID fra processi PowerShell produce
un mismatch su Linux. Correzione: rimozione mirata con Force e identità immutabile del kernel
(`/proc/<pid>/stat`, campo starttime), mantenendo controllo dell'eseguibile. Windows continua
ad usare l'istante di creazione del processo. Stop/reset richiedono una nuova run prima di essere
considerati verificati. Nessuna di queste prove remote rende Docker locale funzionante.

## Seconda run e verifica aggiuntiva del logging

La run [39905665082](https://github.com/MarcoMengoli/OpenTelemetryPlayground/actions/runs/39905665082)
sul commit integrato `3204ed5` (B `c8c9dbe`) è verde: restore, guard, build, test, start, ready,
verify (7 passati), stop e reset. Tuttavia l'ispezione degli artefatti ha evidenziato log limitati
all'avvio: la redirezione di Start-Process dipendeva dalla sessione breve di `lab.ps1`.

Correzione aggiuntiva: `scripts/run-host.ps1` conserva la redirezione per tutta la vita del processo
host. `stop` ora verifica esplicitamente la presenza di `Host stopped` dopo l'uscita del processo.
Serve una nuova run per certificare anche questa verifica più forte, senza confondere un comando
stop riuscito con una prova dei log di shutdown.
