# WarehouseResponse — US09

Playground didattico locale: una sola istanza del lettore e del dispatcher per ambiente.
Contratti [v1](contracts/README.md) invariati; nessuna dipendenza nuova.

## Percorso e persistenza

`ResponseWorker` scandisce `warehouse/responses/*.json` ogni 500 ms, ignorando i
temporanei. Il lease `.response-reader.lock` è distinto da quello dello scrittore:
il lettore non elimina temporanei del simulatore.

`ResponseReader` valida JSON UTF-8 rigoroso e persiste il payload business originale
nel proprio `data/WarehouseResponse.Service/state.db`, tabella `responses`.
La chiave è WarehouseReference; ShipmentId e ShippedAt devono essere compatibili.
Copie rinominate o con un offset diverso dello stesso istante sono duplicati.
Stessa chiave con spedizione/data diversa va in `errors/responses` senza alterare
l'originale. JSON malformato, campi extra e UTF-8 invalido sono quarantinati con
nomi UUID, mai sovrascritti. Il motivo resta nei log, non nel file.

`ResponseLookup` chiama esclusivamente il GET interno di Fulfillment.
`Services:FulfillmentBaseUrl` è opzionale; default localhost, RabbitPort + 14.
Non legge il database Fulfillment. La transazione che salva OrderId e stato Ready
crea anche l'outbox ShipmentConfirmation. MessageId e OccurredAt sono generati
una volta; WarehouseReference, ShipmentId e ShippedAt restano quelli originali.
Un secondo riferimento associato allo stesso OrderId è rifiutato.

`OutboxPump`, `DurableOutbox` e `ConfirmedPublisher` riusano la base integrata US04:
publish persistente, mandatory, conferma broker, poi sent=1. Eventuali riconsegne
di trasporto hanno identico MessageId/body e richiedono deduplicazione nel consumer.
L'archivio `processed/responses` è separato: può precedere il publish perché DB
e outbox rendono già recuperabile il lavoro. Un arresto prima dell'archivio
lascia una copia rileggibile senza una seconda conferma business.

## Errori e recupero esplicito

Timeout HTTP 5 s; massimo tre tentativi persistiti. Retry su rete/timeout,
404 (mapping non ancora disponibile), 408, 429 e 5xx; attese 1/2/4 s.
Il polling riprende soltanto dopo retry_after; un arresto durante HTTP conserva
il tentativo e una pausa conservativa di 4 s. Al terzo errore, o alla ripartenza
dopo il terzo tentativo interrotto, lo stato diventa Rejected. Gli altri 4xx,
JSON/mapping incompatibile sono permanenti. Nessun OrderId viene inventato.
La copia acquisita resta nel DB anche se il file non è più nella directory.

Errori di lettura/archivio conservano l'input e hanno un budget durabile per nome
in file_failures. Dopo tre errori il file richiede intervento operatore. Un file
valido differente può procedere. Errori generali di storage fermano il worker dopo
tre tentativi e sono segnalati con ILogger; la readiness della base è solo
infrastrutturale e non certifica lo stato dei worker business.

Il publisher conserva l'outbox anche a budget esaurito (publish_retry_exhausted).
Il riavvio non azzera i budget. A broker ripristinato, oppure dopo correzione del
mapping/percorso, arrestare il solo ambiente interessato e ispezionare il DB.
Con sqlite3 sul DB di WarehouseResponse, scegliere la sola identità interessata:

```sql
SELECT reference,state,attempts,last_error,retry_after FROM responses;
SELECT message_id,order_id,attempts,sent,last_error FROM outbox;
SELECT * FROM file_failures;
-- Solo dopo ripristino del broker: sostituire MESSAGE_UUID con l'ID letto sopra.
UPDATE outbox SET attempts=0,last_error=NULL
WHERE message_id='MESSAGE_UUID' AND sent=0;
-- Solo dopo risoluzione della causa del lookup: sostituire WAREHOUSE_REFERENCE.
UPDATE responses SET state='PendingLookup',attempts=0,last_error=NULL,
  retry_after='0001-01-01T00:00:00.0000000+00:00'
WHERE reference='WAREHOUSE_REFERENCE' AND state='Rejected';
-- Solo dopo correzione del percorso/permessi: sostituire BASENAME.json.
DELETE FROM file_failures WHERE name='BASENAME.json';
```

Riavviare lo stesso ambiente: la risposta acquisita viene ripresa dal DB anche se
già quarantinata; il file in errors rimane una prova storica. Non cambiare
MessageId, OrderId, payload o spedizione per aggirare l'idempotenza. Invalidi mai
acquisiti possono essere corretti e ricopiati con il protocollo atomico US08.
Un avvio completo richiede RabbitMQ per il lifecycle infrastrutturale; un guasto
successivo lascia il lavoro nel DB. Nessuna ripresa automatica infinita.

## Verifiche ripetibili e isolamento

```powershell
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet test --no-build -c Release
dotnet test --no-build -c Release --filter FullyQualifiedName~WarehouseResponseTests
./scripts/lab.ps1 start  -Name us09-b -PortBase 18700
./scripts/lab.ps1 ready  -Name us09-b -PortBase 18700
./scripts/lab.ps1 verify -Name us09-b -PortBase 18700
./scripts/lab.ps1 stop   -Name us09-b -PortBase 18700
./scripts/lab.ps1 reset  -Name us09-b -PortBase 18700
```

Usare la coppia Name/PortBase solo se libera. Il test broker viene abilitato da
verify (`PLAYGROUND_BROKER_TEST=1`) e richiede Docker Linux. Crea un **altro**
container RabbitMQ `us09-<uuid>`, vhost proprio, porta AMQP dinamica su loopback,
directory temporanea UUID e nessuna porta management. Ferma/riavvia e rimuove
con volumi esclusivamente quel container, anche dopo errore del test.
Il download dell'immagine fissata rabbitmq:4.1.4-management richiede rete/cache.
Deadline: 60 s per comando Docker, 90 s avvio broker, 15 s ricezione; publish
e HTTP usano il timeout applicativo di 5 s dove eseguiti dal dispatcher.

I test offline usano directory/SQLite reali e l'endpoint HTTP reale di Fulfillment,
con middleware deterministico solo per gli errori. Verificano identità, conteggi,
quarantena, rollback della transazione e budget dopo ricreazione dei componenti.
Il test broker osserva ShipmentConfirmation con un consumer di test: conferma,
interruzione dopo publish prima di sent/archive, riconsegna identica, arresto
effettivo del broker, esaurimento e ripresa esplicita. Le finestre di crash sono
simulate interrompendo i passaggi e ricreando i componenti sullo stesso DB,
non mediante kill del processo. Non è una verifica del flusso completo Orders:
US10 e US11 richiedono entrambi i percorsi integrati.
