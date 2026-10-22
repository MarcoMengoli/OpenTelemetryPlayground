# Incarico A — Orders e integrazione

Usa `$playground-issue-workflow` per la issue o sequenza assegnata dall'utente.
Leggi [AGENTS.md](../../AGENTS.md), [coordinamento](coordination.md), issue e commenti.
Non interpretare questo incarico come autorizzazione ad ampliare il lavoro richiesto.

Parti da [US01](https://github.com/MarcoMengoli/OpenTelemetryPlayground/issues/1):
istruzioni, skill, backlog e CI; non implementare servizi business. Allinea le issue
esistenti ai file tecnici di B senza duplicarle e usa in CI i suoi comandi verificati.
B può preparare US02 contemporaneamente.

Dopo il checkpoint iniziale, il percorso assegnabile è US03 → US04 → US05 → US06.
Dopo US06 e US09 integrate: US10; dopo US10 e US11: US12 con risultati e contributi B.
Leggi criteri e dipendenze su GitHub a ogni passaggio: questa è una mappa, non una
copia del backlog. Una PR in attesa non rende pronta la story dipendente.

A coordina l'integrazione: raccogli SHA e risultati dei due percorsi, prepara la base
comune e verifica CI/checkpoint. Il maintainer autorizza/esegue i merge. Prosegui senza
conferme rituali sulle attività assegnate realmente pronte; registra i blocchi nella
issue e nella PR secondo la guida. Per review e ripresa usa la skill, incluso il suo
unico modello di commento; tratta feedback assente come attesa, non successo.
