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
