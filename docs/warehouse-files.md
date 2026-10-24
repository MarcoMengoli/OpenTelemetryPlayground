# US08 — file magazzino e simulatore

Playground locale: una sola istanza per servizio/ambiente, filesystem locale e SQLite.
Nessun SDK OpenTelemetry/Aspire e nessun campo diagnostico aggiunto ai file.
Questa story realizza Fulfillment → file richiesta → simulatore → file risposta.
WarehouseResponse e ShipmentConfirmation restano a US09; il flusso completo a US10.

## Dati, identità e recupero

Il writer legge la file-outbox US07 soltanto dopo il commit di mapping e stima.
Il JSON resta WarehouseRequest v1: schemaVersion, warehouseReference, items,
shippingAddress, courierCode, estimatedDays. Il lookup HTTP di Fulfillment continua
a restituire WarehouseReference/OrderId anche dopo riavvio.

Il simulatore salva in SQLite una riga di warehouse_shipments per WarehouseReference,
con richiesta originale, risposta, istante di acquisizione e stato di pubblicazione.
ResponseJson contiene soltanto schemaVersion, warehouseReference, shipmentId e shippedAt.
ShipmentId/ShippedAt sono creati una sola volta e salvati prima di archiviare l'input.
Una copia identica, anche con altro nome, riprogramma la stessa risposta;
un payload incompatibile per quel riferimento va in quarantena. Formattazione JSON
e scale decimal equivalenti non creano conflitti; l'ordine degli items è significativo.

Entrambi i publisher scrivono UTF-8 in `<nome>.json.tmp-<uuid>` nella directory finale,
eseguono flush su disco, chiudono e fanno rename senza overwrite.
I nomi sono request/response + hash del riferimento business; nessun path esterno
viene interpolato nel nome. I lettori considerano solo `*.json`.
Un file finale già presente con byte equivalenti è successo idempotente;
contenuto differente blocca l'output senza sovrascriverlo. Il confronto è esatto
per i file prodotti da questi writer, che serializzano sempre il medesimo DTO persistito.

La marcatura sent avviene solo dopo il rename. Dopo un arresto:

- prima del rename: l'outbox ricrea il file; il temporaneo non è elaborabile;
- dopo rename ma prima di sent: file identico già presente = successo; se il lettore
  l'ha già acquisito, si ricrea una copia della stessa richiesta/risposta;
- dopo acquisizione del simulatore ma prima dell'archivio: rilettura e deduplicazione;
- dopo archivio ma prima della risposta: l'outbox del simulatore conserva tutto il lavoro.

Il simulatore archivia in processed/requests solo dopo il commit. JSON invalidi,
versioni/campi incompatibili e conflitti finiscono in errors/requests, con nomi UUID
per conservare ogni prova. Il motivo è nel log strutturato, senza alterare il payload.
Processed/errors delle risposte sono riservati a US09.

Ogni worker prende una lease esclusiva FileStream nella propria directory di output.
Solo il titolare, all'avvio, elimina i propri temporanei orfani; non quelli dell'altro
writer o file estranei. Una seconda istanza concorrente non può acquisire la lease.
Il polling è 500ms. Il simulatore pubblica l'outbox anche se la lettura della directory
input fallisce. Ritardo/mancata risposta di un riferimento non bloccano gli altri.

## Guasti I/O e ripresa operatore

SQLite conserva massimo tre errori I/O per file/output, con attesa di 1s e 2s prima
dei tentativi successivi. Tre errori esauriscono il ciclo; il restart non azzera il
budget. Il lavoro e l'eventuale file rimangono presenti. Un conflitto di output è
bloccante al primo riscontro. Non ci sono retry illimitati del file guasto.

Tabelle operative:
- Fulfillment: warehouse_request_file_failures (warehouse_reference, attempts,
  last_error, retry_after), accanto alla warehouse_file_outbox esistente.
- Simulator: warehouse_file_failures (file_key, attempts, last_error, retry_after).
  file_key è `input:<nome-file>` oppure `output:<WarehouseReference>`.

Queste informazioni sono esterne ai JSON business. Un errore di quarantena mantiene
l'input, registra il tentativo e non interrompe i file validi. Se la directory
stessa non è elencabile o il DB non è utilizzabile, il ciclo registra l'errore
infrastrutturale e riprova al polling; non dichiara un'acquisizione riuscita.

Dopo aver corretto la causa, fermare soltanto il servizio dell'ambiente interessato,
ispezionare il file e il relativo record, poi eliminare soltanto il record del budget:

```sql
-- Nel solo DB Fulfillment:
DELETE FROM warehouse_request_file_failures
WHERE warehouse_reference='<riferimento-verificato>';

-- Oppure, nel solo DB Warehouse.Simulator:
DELETE FROM warehouse_file_failures
WHERE file_key='input:<nome-file-verificato>';
DELETE FROM warehouse_file_failures
WHERE file_key='output:<riferimento-verificato>';
```

Riavviare il servizio: l'outbox/input ancora presente viene ripreso. Non cambiare
identificativi o payload per aggirare l'idempotenza. Per riprodurre una risposta già
inviata, ripresentare una copia della richiesta originale nella directory requests:
il simulatore riusa ShipmentId e ShippedAt. In alternativa, da servizio fermo,
impostare sent=0 sulla sola riga warehouse_shipments verificata.
Non usare reset del laboratorio per recuperare un singolo incarico.

## Scenari deterministici

Impostazioni del solo host Warehouse.Simulator, fuori dai file:

| Impostazione | Comportamento |
| --- | --- |
| Simulation:Profile=happy-path | risposta normale |
| Simulation:Profile=slow-warehouse | risposta dopo il ritardo configurato |
| Simulation:DelayMs | default 6000 per slow-warehouse |
| Simulation:Profile=missing-warehouse-response | input acquisito/archiviato, risposta durevole pendente |
| Simulation:Profile=duplicate-warehouse-response | due file completi con lo stesso identico payload |
| Simulation:WarehouseReference | selettore opzionale; omesso=tutti |

Il ritardo parte dall'acquisizione persistita e non riparte dopo restart. Uscire dal
profilo missing-warehouse-response rilascia le risposte pendenti. Duplicati usano un
secondo nome deterministico con suffisso -duplicate, mai una nuova spedizione.
I profili degli altri servizi non attivano questi comportamenti.

## Comandi, isolamento e prove per A / US12

Base: main `9ca4b94`, squash US07 autorizzato, tree identico alla PR #16 verificata.
Comandi della base invariati:

```powershell
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet test --no-build -c Release
python .github/scripts/check_architecture.py --require-restored
python -B -m unittest discover -s .github/tests -v
./scripts/lab.ps1 start -Name us08-b -PortBase 18600
./scripts/lab.ps1 ready -Name us08-b -PortBase 18600
./scripts/lab.ps1 verify -Name us08-b -PortBase 18600
./scripts/lab.ps1 stop -Name us08-b -PortBase 18600
./scripts/lab.ps1 reset -Name us08-b -PortBase 18600
```

Riservare 18600/18601 e 18610–18617; dati/Compose dipendono dal worktree e dal nome.
Mai arrestare/reset di altri ambienti. La CI usa il proprio nome univoco e runner.

WarehouseFileTests usa directory temporanee e SQLite reali per le finestre
flush/rename/sent, acquisizione/archivio, lettura prima della marcatura del writer,
doppie richieste/risposte, JSON invalido/incompleto, conflitti, ritardi, mancata
risposta, errori I/O con budget persistente e lease. Comprende il polling dei due
BackgroundService e lookup HTTP reale. I checkpoint dei metodi lanciano un'eccezione
di test dopo l'effetto reale; non simulano un kill del processo o del sistema.
I profili temporali sono verificati con istanti espliciti rispetto al timestamp
persistito, senza attese casuali. Le prove di arresto dei processi end-to-end restano US11.

Comandi/esiti, SHA finale, CI e review sono registrati nella PR collegata alla issue #8.
Il daemon Docker non è disponibile nella sessione Windows: il test RabbitMQ locale
resta skipped; la CI esistente abilita le prove reali con verify.

## Consegna US09

WarehouseResponse dovrà leggere solo responses/*.json, validare WarehouseResponse
con WireJson, cercare l'ordine tramite lookup HTTP Fulfillment e persistere inbox/outbox
prima di archiviare. File identici con nomi diversi mantengono la stessa identità
business (WarehouseReference + ShipmentId). Non assumere che i file non si ripetano:
un arresto tra rename e sent può ricrearli anche dopo che erano stati acquisiti.