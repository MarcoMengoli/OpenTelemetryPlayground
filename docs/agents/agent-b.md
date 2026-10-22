# Incarico B — Base tecnica e Fulfillment

Usa `$playground-issue-workflow` per la issue o sequenza assegnata dall'utente.
Leggi [AGENTS.md](../../AGENTS.md), [coordinamento](coordination.md), issue e commenti.
Non interpretare questo incarico come autorizzazione ad ampliare il lavoro richiesto.

Parti da [US02](https://github.com/MarcoMengoli/OpenTelemetryPlayground/issues/2)
senza aspettare US01 o la skill: in loro assenza usa la procedura contenuta nella
issue. Possiedi soluzione, infrastruttura, contratti, documentazione tecnica e comandi.
Pubblica presto nella issue/PR i percorsi effettivi, versioni, parametri di isolamento,
comandi verificati e SHA; A li usa per backlog e CI. Non creare una pipeline concorrente.

Dopo il checkpoint iniziale, il percorso assegnabile è US07 → US08 → US09.
Dopo US06 e US09 integrate: US11, in parallelo a US10 su un ambiente distinto.
Consegna ad A i risultati per US12. Leggi criteri e dipendenze su GitHub a ogni passaggio;
non attendere l'altro percorso per testare il singolo servizio con sostituti conformi.
Il test finale usa tutti i servizi reali del laboratorio.

Prosegui senza conferme rituali sulle attività assegnate pronte. Comunica a GitHub
blocchi, cambiamenti condivisi e consegne; le chat non si sincronizzano automaticamente.
A coordina il checkpoint; il maintainer autorizza/esegue i merge. Per review e ripresa
usa la skill e il suo unico modello, senza considerare il silenzio una review conclusa.
