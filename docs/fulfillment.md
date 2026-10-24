# US07 — Couriers e Fulfillment

Playground didattico locale, una sola istanza per servizio e ambiente. Nessun corriere
reale, nessuna instrumentazione OpenTelemetry/Aspire. Il flusso completo resta a US10.
US07 prepara la richiesta persistente; US08 implementerà writer e simulatore.

## Comportamento e persistenza

Couriers espone `POST /api/v1/estimates` usando i DTO e la validazione v1.
Il risultato è `LOCAL-SIM`, 2 giorni per IT e 5 per gli altri paesi.
SQLite conserva richiesta, risultato e contatore per OrderId; lo stesso indirizzo
recupera lo stesso risultato, un indirizzo incompatibile restituisce 409.
JSON invalido/campi extra: 400; errore temporaneo simulato o DB occupato: 503.
Il risultato e il tentativo sono persistiti prima del ritardo della risposta.

Fulfillment consuma `FulfillOrder.v1` con prefetch 1 e ack manuale. Verifica body,
tipo, content type, persistenza e corrispondenza MessageId AMQP/body. La transazione
di acquisizione registra inbox per MessageId e incarico per OrderId con riferimento
magazzino generato stabile. Un duplicato logico con MessageId diverso e medesimo
payload business non crea un secondo incarico. Prezzi decimal equivalenti sono
considerati uguali; l'ordine delle righe resta significativo. Lo stesso MessageId
con OccurredAt o dati business diversi è un conflitto. Invalidi/conflitti vengono
rifiutati senza requeue nella coda errori esistente.

L'incarico passa da Pending a Ready soltanto quando una risposta Couriers valida
per lo stesso OrderId viene salvata insieme al payload WarehouseRequest nella
file-outbox. Mapping e file-outbox sono quindi durabili prima di qualsiasi futuro
file. L'ack avviene dopo questo commit. Un errore non produce una stima fittizia.

Tabelle nel solo `data/Fulfillment.Service/state.db`:

- `fulfillment_inbox`: identità del messaggio e payload originale.
- `fulfillment_work`: OrderId, WarehouseReference unico, richiesta business, stato,
  tentativi e ultimo errore operativo.
- `warehouse_file_outbox`: WarehouseReference, WarehouseRequest JSON e sent=0.

Il lookup `GET /internal/v1/warehouse-requests/{warehouseReference}` restituisce
soltanto WarehouseMappingResponse (200 oppure 404). Nessun accesso cross-service
al DB. L'associazione all'ordine è esterna al payload WarehouseRequest, che contiene
esattamente i sei campi business v1. Nessun file viene scritto nella US07.

## Timeout, arresti e ripresa

Il client HTTP usa timeout 5s, stessa chiave OrderId e massimo tre tentativi durabili,
con pausa prima di ciascuno di 1s/2s/4s. Errori di rete, timeout, 408, 429 e 5xx sono
temporanei. Altri status non riusciti e risposte invalide/incompatibili sono permanenti.
Il budget è riservato su SQLite prima dell'HTTP: anche un arresto durante la chiamata
consuma quel tentativo. Il riavvio non azzera il contatore.

All'avvio della sessione consumer, Pending viene ripreso dal DB; poi il servizio
consuma le riconsegne. Arresti dopo accettazione, dopo HTTP ma prima del commit
oppure dopo Ready ma prima dell'ack non perdono l'incarico. Nell'ultimo caso il
duplicato viene ackato senza una nuova chiamata HTTP. Esito HTTP ambiguo: ripetizione
con lo stesso OrderId recupera il risultato persistito del corriere.

Dopo esaurimento/errori permanenti lo stato è Failed, last_error resta ispezionabile
e il messaggio viene nackato nella coda `<Name>.fulfillment.fulfill.v1.errors`.
Non c'è un loop automatico dalla coda errori. Se l'ultimo tentativo era in corso
all'arresto, il riavvio registra retry_exhausted. Un errore di storage/connessione
durante il consumo lascia la consegna senza ack; la sessione si chiude e viene
ricreata con pausa 2s. Il budget HTTP salvato non viene azzerato.

Ripresa operatore dopo aver corretto la causa: fermare soltanto Fulfillment
dell'ambiente interessato; nel suo DB, con client SQLite, eseguire la transazione
seguente sostituendo l'OrderId verificato (mai cambiare gli identificativi/payload):

```sql
BEGIN IMMEDIATE;
UPDATE fulfillment_work
SET state='Pending', attempts=0, last_error=NULL
WHERE order_id='<OrderId>' AND state='Failed';
COMMIT;
```

Riavviare la stessa istanza: Pending viene ripreso anche senza reinvio broker.
La vecchia copia nella coda errori rimane una prova; un eventuale reinvio conserva
MessageId e payload e verrà deduplicato. Questo è un nuovo ciclo esplicito con
budget tre; non usare reset dell'ambiente per recuperare un singolo incarico.

## Configurazione e comandi per A / US12

`Services:CouriersUrl` sovrascrive l'URL del corriere; default
`http://127.0.0.1:<Lab:RabbitPort + 15>`, coerente con lab.ps1.
Configurare solo l'host Couriers per gli scenari, tramite ambiente o argomenti:

| Impostazione | Effetto |
| --- | --- |
| Simulation:Profile=happy-path | risposta normale |
| Simulation:Profile=slow-courier | ritardo prima della risposta, dopo commit |
| Simulation:DelayMs | ritardo in ms; default 6000 per slow-courier |
| Simulation:Profile=temporary-courier-failure | 503 per i primi N tentativi persistiti |
| Simulation:FailuresBeforeSuccess=N | numero N di risposte 503, default 0 |
| Simulation:OrderId=UUID | applica lo scenario soltanto a questo ordine; omesso=tutti |

Il contatore Couriers include le precedenti richieste dello stesso OrderId.
Per uno scenario nuovo usare un nuovo ordine; riavviare non ricomincia da zero.
Profili destinati agli altri host non attivano guasti Couriers.

Dalla radice del worktree, comandi della base invariati:

```powershell
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet test --no-build -c Release
python .github/scripts/check_architecture.py --require-restored
python -B -m unittest discover -s .github/tests -v
./scripts/lab.ps1 start -Name us07-b -PortBase 18500
./scripts/lab.ps1 ready -Name us07-b -PortBase 18500
./scripts/lab.ps1 verify -Name us07-b -PortBase 18500
./scripts/lab.ps1 stop -Name us07-b -PortBase 18500
./scripts/lab.ps1 reset -Name us07-b -PortBase 18500
```

Verificare prima che 18500, 18501 e 18510–18517 siano libere. Dati e Compose sono
isolati dal worktree e dal nome us07-b. Stop/reset si applicano soltanto a questa
coppia Name/PortBase. Il laboratorio avviato richiede Docker Linux funzionante.

I test offline US07 usano Kestrel su porte assegnate dal sistema e SQLite in directory
UUID temporanee. I quattro test broker sono abilitati da `verify`: exchange/code UUID
separati nello stesso vhost, nessun consumo delle code applicative dell'ambiente.
Il produttore di test usa FulfillOrder e proprietà AMQP v1. I test esaminano outbox,
mapping, contatori e coda errori, con duplicati/conflitti, timeout reale, fallimenti
N-volte e chiusura del canale prima/dopo HTTP e dopo commit prima dell'ack.
Questo verifica il percorso RabbitMQ–Fulfillment–Couriers, senza dipendere da Orders.
I test chiudono canali/host e cancellano solo risorse create dal singolo test.

## Consegna US08

`FulfillmentStore.UnsentFiles()` è il punto di lettura della file-outbox;
ogni entry ha WarehouseReference stabile e JSON WarehouseRequest validato.
US08 aggiungerà pubblicazione temporaneo/rename e marcatura sent dopo pubblicazione
idempotente, senza cambiare questi contratti. Il mapping esiste già, anche per Pending,
e il lookup resta disponibile dopo riavvio. Non usare l'ordine di arrivo dei file
per ricavare OrderId.

## Verifiche della consegna

Base integrata: `64f566f`, successiva al checkpoint US01/US02 `774dd6e`
([CI checkpoint](https://github.com/MarcoMengoli/OpenTelemetryPlayground/actions/runs/40077885763)).
Comandi, SHA finale, CI e stato review aggiornati nella PR US07 collegata alla issue #7.
Il Docker daemon Windows non è disponibile nella sessione locale: le prove RabbitMQ
non vengono dichiarate eseguite localmente. La CI esistente abilita i test reali
con verify; uno skip offline non equivale a successo d'integrazione.