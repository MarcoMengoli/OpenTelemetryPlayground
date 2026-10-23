# Eseguire una issue o una sequenza

Usare `$playground-issue-workflow` per la issue o sequenza richiesta dall'utente.
Leggere [AGENTS.md](../../AGENTS.md), [integrazione](coordination.md), specifica e
commenti della issue. Non ampliare autonomamente il backlog o il lavoro richiesto.

US01 prepara istruzioni, skill, backlog e CI. US02 prepara soluzione, infrastruttura,
contratti e comandi: può partire senza attendere la skill, usando la procedura nella
issue. La consegna tecnica deve indicare percorsi reali, SHA e verifiche eseguite;
la CI riusa gli stessi comandi. Non creare interfacce o pipeline concorrenti.

Dopo il checkpoint US01/US02, i due percorsi sono:

- Ordini e pagamenti: US03 → US04 → US05 → US06.
- Evasione e magazzino: US07 → US08 → US09.
- Dopo US06 e US09 integrate: US10 e US11 su ambienti separati; dopo entrambe, US12.

Questa mappa non sostituisce il backlog: leggere dipendenze e ambito aggiornati su
GitHub prima di scegliere la prossima issue richiesta. Una PR aperta non soddisfa
una dipendenza di integrazione. I test di servizio possono usare sostituti conformi
ai contratti; la verifica finale usa tutti i servizi reali del laboratorio.

Seguire la skill per presa in carico, worktree/branch, implementazione/verifiche,
commit/push, PR e review, incluso il suo unico modello di commento. Proseguire senza
conferme rituali sul lavoro richiesto e pronto; registrare blocchi e passaggi di
consegna nella issue/PR. Le chat non si sincronizzano automaticamente.
Il maintainer autorizza/esegue i merge; CI e review non sostituiscono l'autorizzazione.
