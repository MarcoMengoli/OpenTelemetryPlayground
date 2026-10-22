---
name: playground-issue-workflow
description: Implementare o riprendere una issue o sequenza assegnata di MarcoMengoli/OpenTelemetryPlayground fino alla PR e gestione della review Codex. Non usare per semplici domande sul progetto.
---

# Workflow issue del playground

Leggere [AGENTS.md](../../../AGENTS.md) e l'incarico in `docs/agents/`;
la issue completa, commenti inclusi, definisce lo scope. Usare strumenti GitHub
connessi o `gh`; non dipendere da tool esclusivi di una sessione.

1. **Riprendere prima di creare.** Leggere dipendenze, commenti di presa in carico,
   worktree, branch locali/remoti e PR aperte/chiuse collegate alla issue.
   Riutilizzare il lavoro proprio; non appropriarsi di quello dell'altro agente.
   Per una sequenza autorizzata scegliere la prossima issue assegnata con dipendenze
   integrate e verificate. Non ampliare il backlog. US01 e US02 possono partire
   in parallelo; le successive richiedono il checkpoint iniziale.
2. **Preparare.** Dichiarare la presa in carico nella issue; usare un worktree
   isolato e branch `codex/usNN-descrizione` dalla base di integrazione aggiornata.
   Isolare anche ambiente, porte, Compose, broker, database e directory secondo B.
   Una dipendenza in PR non è integrata: registrare il blocco con riferimento,
   risultato mancante e condizione di sblocco; proseguire solo su lavoro indipendente.
3. **Implementare e verificare.** Rispettare proprietà dei file e contratti.
   Eseguire i comandi pertinenti della base tecnica; correggere i difetti e registrare
   commit, comandi, risultati e limiti. Non dichiarare passato un test non eseguito.
4. **Consegnare.** Creare commit coerenti, push e una PR collegata alla issue
   (normalmente una per issue), oppure aggiornare la PR esistente. Usare il template
   PR: comportamento, verifiche, semplificazioni, dipendenze e stato review.
5. **Review.** Prima del commento leggere review, commenti, reazioni e controlli della
   PR: evitare richieste duplicate, incluse review automatiche equivalenti.
   Se manca una richiesta equivalente, usare il modello sotto. Registrare SHA della
   versione richiesta/esaminata. Distinguere **richiesta**, **in corso** (presa in
   carico), **completata** (feedback effettivo), **non disponibile** (limite esplicito).
   Senza risposta resta richiesta/in attesa, mai superata; una review precedente
   non certifica automaticamente i commit successivi. L'accesso GitHub non prova
   che Codex Code review sia abilitato.
6. **Gestire i rilievi.** Confrontarli con issue, contratti e Code Review Rules.
   Correggere quelli pertinenti sullo stesso branch, rieseguire i controlli coinvolti
   e fare push. Motivare nella PR quelli fuori scope. Richiedere un altro passaggio
   solo per modifiche sostanziali o dubbi concreti, non per preferenze stilistiche.
7. **Passaggio di consegna.** Aggiornare issue/PR con CI, review, dipendenze residue
   e prossimo responsabile. Integrare solo con autorizzazione già ricevuta; chiudere
   la issue soltanto dopo integrazione e verifiche previste. Durante l'attesa lavorare
   su issue realmente indipendenti già assegnate, senza monitoraggi permanenti o
   polling continuo impliciti. Non scambiare review locale per review GitHub.

## Modello unico di richiesta review

```text
@codex review

Questo repository è un playground didattico locale, non un ecommerce di produzione. Valuta correttezza, coerenza con la issue e verificabilità. Non richiedere funzionalità di produzione fuori scope. La versione iniziale deve restare priva di instrumentazione OpenTelemetry e dipendenze Aspire.

Applica le Code Review Rules in AGENTS.md e i contratti collegati alla issue. Privilegia problemi concreti e correzioni proporzionate alle semplificazioni documentate.
```

[Riferimento ufficiale review GitHub](https://learn.chatgpt.com/docs/third-party/github).
