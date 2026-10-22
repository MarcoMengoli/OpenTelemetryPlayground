# OpenTelemetryPlayground

Playground ecommerce locale per imparare OpenTelemetry **in un secondo momento**.
La issue assegnata e i suoi commenti sono la specifica; backlog e avanzamento restano
su [GitHub](https://github.com/MarcoMengoli/OpenTelemetryPlayground/issues).

## Regole del laboratorio

- Niente SDK/exporter OpenTelemetry, Collector, dashboard, dipendenze Aspire,
  ActivitySource/Meter personalizzati o propagazione manuale di trace context/baggage.
  Non sopprimere la diagnostica interna del runtime o delle librerie.
- Solo identificativi business nei contratti magazzino e nella mappa di associazione:
  niente TraceId, SpanId o altri metadati diagnostici. Log essenziali con ILogger<T>
  e Serilog console; codice esplicito e pochi livelli.
- Disponibilità Inventory garantita: prenotazione ed evasione procedono in parallelo.
  Pagamenti, fatture, corrieri e magazzino sono simulati; nessuna UI.
- Persistenza, idempotenza, outbox e retry limitati richiesti dalla singola issue
  appartengono alla sua implementazione, non vanno rinviati alle verifiche finali.

## Responsabilità e fonti

- A: questo file, `docs/agents/`, `.agents/skills/playground-issue-workflow/`,
  `.github/` e backlog GitHub. B: soluzione, SDK/dipendenze, `src/`, `tests/`,
  infrastruttura/script, README e documentazione tecnica della base US02.
- Nei percorsi successivi vale l'ambito della singola issue. Contratti e componenti
  condivisi restano coordinati con B: proporre cambiamenti e impatto sui due percorsi
  prima di modificarli; non inventare payload alternativi.
- Usare la skill [$playground-issue-workflow](.agents/skills/playground-issue-workflow/SKILL.md)
  per implementare/riprendere una issue. Incarichi: [A](docs/agents/agent-a.md),
  [B](docs/agents/agent-b.md); [coordinamento e verifiche](docs/agents/coordination.md).
- A coordina il checkpoint di integrazione; il maintainer autorizza/esegue i merge.
  Una PR aperta, CI verde o review conclusa non autorizza il merge né la chiusura issue.

## Verifiche

Usare soltanto i comandi della base tecnica US02 registrati nella
[guida di coordinamento](docs/agents/coordination.md#base-tecnica-e-verifiche).
Annotare commit, comando, ambiente ed esito; prerequisito assente significa
**non verificato**, mai superato. Worktree separati non isolano porte e dati.

## Code Review Rules

Valutare correttezza, coerenza con la issue/contratti e verificabilità di un
playground didattico locale. Privilegiare difetti riproducibili: perdita/duplicazione
di effetti, stati errati, contratti incompatibili, isolamento/reset pericolosi,
segreti esposti e verifiche mancanti rispetto allo scope. Segnalare instrumentazione
OTel o dipendenze Aspire introdotte dall'applicazione.
Non richiedere scalabilità, alta disponibilità, pagamenti reali, conformità fiscale
o altre funzioni di produzione. Le semplificazioni dichiarate non sono difetti.
