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

A aggiorna qui i riferimenti tecnici e i comandi dopo la consegna di B, e li riusa
identici nella pipeline. Finché manca la base, la CI deve rendere esplicita la
dipendenza: non saltare build/test per produrre un falso verde.

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
