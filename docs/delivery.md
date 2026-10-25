# Baseline senza OpenTelemetry — US12

Il riferimento immutabile della **baseline applicativa integrata** è
[`c9f2a065716c657130533225e42ccc0a721454b0`](https://github.com/MarcoMengoli/OpenTelemetryPlayground/commit/c9f2a065716c657130533225e42ccc0a721454b0).
È lo squash US10 #23 sulla main che include già US11 #22 e tutti i prerequisiti.
Non è l'head preliminare afcd7ce della PR US10 né l'head 100dee3 della PR US11.
Per recuperare il codice in un checkout separato, senza alterare il proprio lavoro:

```powershell
git fetch origin main
git worktree add --detach ../playground-baseline c9f2a065716c657130533225e42ccc0a721454b0
```

La guida US12 è un'aggiunta documentale compatibile con questa baseline; non cambia
servizi, contratti, pacchetti o politiche di recupero. Il suo commit di integrazione
verrà registrato nella [issue US12](https://github.com/MarcoMengoli/OpenTelemetryPlayground/issues/12)
e nella PR collegata soltanto dopo merge autorizzato. Non si presenta un head in PR
come commit finale integrato. Nessun tag è previsto o creato dal processo corrente.

## Verifiche della base comune

[CI main 40185346253](https://github.com/MarcoMengoli/OpenTelemetryPlayground/actions/runs/40185346253)
su c9f2a06, Ubuntu con SDK10.0.400:

- restore locked, build Release **0 warning/errori**, guard su dipendenze risolte e 6 test guard;
- offline **114 passati/30 broker skipped**, distinti dalla verifica reale;
- `lab.ps1 start/ready/verify/stop/reset`: **144 passati/0 skipped**, inclusi gli otto
  scenari US11 con tutti gli host reali; [log laboratorio](https://github.com/MarcoMengoli/OpenTelemetryPlayground/actions/runs/40185346253/artifacts/12596087461);
- `e2e.ps1 -Name us10-40185346253-1 -PortBase 18500 -NoBuild`: due esecuzioni reali
  normale/rifiuto, identità distinte, Inventory atteso separatamente, riconciliazione
  magazzino e cleanup dei soli ambienti creati. [Evidenze ecommerce](https://github.com/MarcoMengoli/OpenTelemetryPlayground/actions/runs/40185346253/artifacts/12595263884).

La CI della PR US12 esegue inoltre direttamente i due esempi marcati della
[guida](getting-started.md): HTTP fino alla fattura con retry identico e ripetizione
del test US11 di risposta mancante/recupero del simulatore. Esiti e SHA di questa
verifica documentale sono riportati nella issue/PR US12, senza dedurli dalla sola
verifica della base. Gli artifact hanno retention 7 giorni: i riferimenti Git e i
risultati registrati nelle issue restano consultabili anche dopo la loro scadenza.

## Story e guide integrate

| Story | PR integrata | Riferimento operativo |
| --- | --- | --- |
| US01 / US02 | [#13](https://github.com/MarcoMengoli/OpenTelemetryPlayground/pull/13), [#14](https://github.com/MarcoMengoli/OpenTelemetryPlayground/pull/14) | [Workflow](agents/workflow.md), [contratti](contracts/README.md) |
| US03 | [#15](https://github.com/MarcoMengoli/OpenTelemetryPlayground/pull/15) | [Payments](payments.md) |
| US04 | [#17](https://github.com/MarcoMengoli/OpenTelemetryPlayground/pull/17) | [Checkout/Orders](checkout-orders.md) |
| US05 | [#20](https://github.com/MarcoMengoli/OpenTelemetryPlayground/pull/20) | [Inventory](inventory.md) |
| US06 | [#21](https://github.com/MarcoMengoli/OpenTelemetryPlayground/pull/21) | [Completamento](order-completion.md) |
| US07 | [#16](https://github.com/MarcoMengoli/OpenTelemetryPlayground/pull/16) | [Fulfillment/Couriers](fulfillment.md) |
| US08 | [#18](https://github.com/MarcoMengoli/OpenTelemetryPlayground/pull/18) | [File magazzino](warehouse-files.md) |
| US09 | [#19](https://github.com/MarcoMengoli/OpenTelemetryPlayground/pull/19) | [WarehouseResponse](warehouse-response.md) |
| US10 | [#23](https://github.com/MarcoMengoli/OpenTelemetryPlayground/pull/23) | [Normale/rifiuto due volte](end-to-end.md) |
| US11 | [#22](https://github.com/MarcoMengoli/OpenTelemetryPlayground/pull/22) | [Guasti, deadline e recupero](failure-scenarios.md) |

Le review Codex finali US10 e US11 sono completate senza rilievi sui rispettivi
head verificati; il workflow della base comune verifica i contenuti integrati insieme.
La review della PR documentale US12 ha uno stato distinto, registrato nella stessa PR.

## Limiti da mantenere negli esercizi successivi

Playground locale a singola istanza per servizio; Inventory disponibile, prezzi
inseriti dal client, pagamenti/fatture/corrieri/magazzino simulati, nessuna UI o
conformità fiscale. Non si promettono scalabilità, alta disponibilità o atomicità
distribuita. Gli adattatori HTTP US11 nei punti capture/invoice trattengono una
risposta/richiesta dopo/prima degli effetti reali dichiarati. I kill effettivi sono
distinti dai checkpoint in-process di commit/publish/ack/flush/rename, che non
dimostrano power loss. Il catalogo US11 mantiene questa distinzione e il recupero.

Il laboratorio reale è stato verificato in CI Linux. Le verifiche locali macOS di A
e quelle senza Docker di B non vengono presentate come esecuzioni reali complete.
Readiness è infrastrutturale; Completed non certifica da solo Inventory. Un timeout
non è un rifiuto business e un restart non rinnova budget esauriti.

Guard e review controllano assenza di SDK/exporter OTel, Collector, Aspire e
instrumentazione applicativa personalizzata. File/mapping conservano solo dati
business; ILogger/Serilog resta essenziale, senza sopprimere diagnostica del runtime.
Non sono aggiunti esercizi implementati di instrumentazione o backend di telemetria.
