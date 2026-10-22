# Coordinamento dei due percorsi

La [US01](https://github.com/MarcoMengoli/OpenTelemetryPlayground/issues/1) definisce
il processo; la [US02](https://github.com/MarcoMengoli/OpenTelemetryPlayground/issues/2)
è la fonte della base tecnica. Backlog, blocchi e avanzamento vivono nelle issue/PR,
non in un secondo tracker locale. Le chat indipendenti non si sincronizzano da sole.

## Consegne e file condivisi

Per presa in carico, blocco o consegna, aggiungere nella issue e richiamare nella PR:
**responsabile; branch/PR/SHA; risultato o blocco; comandi ed esiti; dipendenza e
condizione di sblocco; prossimo responsabile**. Distinguere eseguito, fallito e non
verificato con motivo. Non duplicare commenti invariati.

La proprietà generale è in [AGENTS.md](../../AGENTS.md); i servizi e test successivi
seguono la singola issue. Contratti, dipendenze, soluzione, script e configurazione
condivisi richiedono una proposta con impatto su entrambi i percorsi e coordinamento
con B. Le correzioni nel codice dell'altro percorso vanno consegnate al proprietario.
US10 possiede le prove complete normali/rifiuto; US11 le prove di guasto; US12
consolida il README con B. A mantiene workflow GitHub e istruzioni.

## Isolamento reale

Un worktree/branch per issue; riprendere quelli esistenti prima di crearne altri.
Prima di start/test dichiarare nella issue/PR l'ambiente e verificare che differisca
da quelli attivi dell'altro agente:

| Risorsa | Requisito |
| --- | --- |
| Compose e container | Nome progetto distinto; nessun nome container globale condiviso |
| Porte host | Tutte le porte pubblicate distinte, inclusi HTTP e gestione broker |
| Broker | Istanza/vhost o namespace di exchange, code e code errori isolato secondo B |
| Persistenza | Volumi e database/file separati per ambiente e servizio |
| Magazzino | Radice distinta con requests, responses, processed, errors |
| Stop/reset | Solo risorse dell'ambiente scelto, mai prune globale o dati dell'altro agente |

Usare nomi, variabili e comandi effettivamente predisposti da B; non introdurre un
secondo schema di configurazione. Registrare anche le risorse usate dai test e la loro
pulizia. Un test non avviabile per conflitto/prerequisito è non verificato.

## Base tecnica e verifiche

Sulla base iniziale `main` (`63da890`) sono presenti solo README e licenza.
Comandi .NET, test RabbitMQ, contratti e parametri di isolamento dipendono dalla
consegna US02: non sono ancora verificati in questa base. Il collegamento alla issue
US02 è intenzionale finché B non pubblica percorsi reali; non usare nomi inventati.

La [consegna preliminare di B](https://github.com/MarcoMengoli/OpenTelemetryPlayground/issues/2#issuecomment-6353293088)
propone SDK da `global.json`, `dotnet restore --locked-mode`,
`dotnet build --no-restore -c Release`, `dotnet test --no-build -c Release` e
`./scripts/lab.ps1 start|ready|verify|stop|reset -Name us01-a -PortBase 18300`
(ogni azione è una chiamata distinta in PowerShell 7). La pipeline è predisposta
su queste chiamate; gli esiti e i riferimenti finali dipendono ancora da US02.

Il workflow [ci.yml](../../.github/workflows/ci.yml) usa runner Ubuntu, SDK fissato
da B e ambiente univoco per run. Esegue restore, guard architetturale, build, test,
start/readiness e verify con broker reale, poi stop, raccolta log e reset del solo
ambiente CI anche in caso di errore. Se US02 manca, il job fallisce esplicitamente:
non salta build/test per produrre un falso verde. Il job separato `guard-tests`
verifica il controllo statico, senza certificare la base tecnica.

Controlli A disponibili: `python -B -m unittest discover -s .github/tests -v` e,
dopo restore, `python .github/scripts/check_architecture.py --require-restored`.
Il guard legge manifest e dipendenze risolte e cerca possibili violazioni nel codice;
la review resta necessaria per propagazione manuale e altri casi non riconoscibili
staticamente. A aggiorna questa sezione con la consegna verificata di B.

## Checkpoint e integrazione

A raccoglie i risultati e coordina una base comune identificata da SHA; il maintainer
autorizza/esegue i merge. Per verificare insieme PR ancora aperte si può usare un
checkout temporaneo di integrazione: documentare entrambi gli SHA e non confonderlo
con integrazione su main o autorizzazione al merge.

US03/US07 partono soltanto dopo US01 e US02 integrate e verificate insieme:
soluzione compilabile, ambiente avviabile, logging, contratti, test base, pipeline e
backlog allineati. US10/US11 richiedono i due percorsi integrati; US12 entrambe le
verifiche. Le sostituzioni conformi ai contratti sono ammesse nei test di percorso,
non nella prova finale. Review/CI e chiusura issue seguono la skill.
