# Completamento ordine — US06

Orders.Service consuma `ShipmentConfirmation.v1` dalla coda
`<Name>.orders.shipment-confirmation.v1`, con UTF-8/JSON v1 e proprietà AMQP
validate, prefetch 1 e ack manuale. Un ordine sconosciuto, non ancora autorizzato
o rifiutato non viene addebitato: la conferma va nella coda errori prevista.

## Validazione e persistenza

Le nuove tabelle appartengono soltanto al DB Orders, accanto allo schema US04:

- `shipment_inbox`: MessageId univoco, corpo originale, OrderId, associazione
  verificata, elaborazione finale e tentativi/errori del lookup;
- `shipment_work`: OrderId univoco, WarehouseReference e ShipmentId univoci,
  ShippedAt originale, CaptureId/InvoiceId e budget separati di capture/fattura.

L'inizializzazione crea queste tabelle anche su un DB US04 esistente, senza
eliminare dati o cambiare le sue colonne. `OrdersStore` rimane il proprietario
unico; il codice della nuova parte è in ShipmentStore.cs.

Prima di passare a Shipped, Orders verifica che WarehouseReference appartenga
all'OrderId ricevuto attraverso il GET Fulfillment già previsto dalla US02.
`Services:FulfillmentBaseUrl` configura l'host; default locale base broker +14.
Non accede al DB Fulfillment e non modifica contratti, file o servizi del percorso B.
Il riferimento viene URL-encoded e la risposta deve avere entrambi gli ID attesi.
404/408/429/5xx/rete hanno retry limitati; 400/409, risposta invalida o mapping
incoerente sono permanenti per quella ricevuta. Una ricevuta respinta prima del
binding non prenota la chiave business dell'ordine: una conferma successiva valida
può ancora completarlo. I rifiuti rimangono visibili nel registro e nella DLQ.

Una transazione lega spedizione, ordine Shipped e ricevuta verificata. I duplicati
logici con nuovo MessageId devono conservare WarehouseReference, ShipmentId e
ShippedAt; un MessageId già visto deve conservare anche il corpo completo, incluso
OccurredAt. Una spedizione già verificata e identica non richiede un nuovo lookup.

## Addebito, fattura e consultazione

Il processor della singola istanza serializza le proprie chiamate, come il consumer
con prefetch 1. Non introduce coordinamento multiistanza o transazioni distribuite.

1. **Shipped**: POST Payments `/api/v1/captures` con OrderId e AuthorizationId
   già persistito. La chiave resta AuthorizationId anche dopo timeout o arresto.
2. **Captured**: dopo la risposta valida, commit atomico di CaptureId e stato.
   Soltanto allora POST `/api/v1/invoices` con il CaptureId persistito.
3. **Completed**: commit atomico di InvoiceId, stato, LastError null e ricevute
   verificate elaborate. Ack solo dopo questo commit, oppure per un duplicato
   identico già completato.

Le risposte Payments devono avere identità e totale originali. JSON invalido,
ID/importo incoerenti e HTTP permanenti bloccano il passo raggiunto. Capture e
fattura restano operazioni pubbliche separate; non si crea un nuovo ID per
aggirare un risultato incerto. Nessuna modifica Payments è stata necessaria.

`GET /internal/v1/orders/{orderId}`, e la consultazione pubblica delegata da
Checkout, restituiscono OrderSnapshot con ShipmentId da Shipped, CaptureId da
Captured e InvoiceId soltanto dopo il commit Completed. Errori operativi stanno
in LastError, non in FailureCode o in un nuovo stato terminale. Un Completed
mantiene LastError null; eventuali errori storici delle outbox US04 restano nelle
rispettive righe outbox, senza invalidare il DTO di completamento.

## Budget, arresti e ripresa

HTTP usa timeout 5 secondi e massimo tre tentativi durevoli per lookup della
ricevuta, capture dell'ordine e fattura dell'ordine, con pause 1/2/4 secondi.
`shipment_mapping_retry_exhausted`, `capture_retry_exhausted` e
`invoice_retry_exhausted` distinguono l'esaurimento. HTTP permanenti e risposte
incoerenti conservano errore e flag blocked; nessun retry automatico al riavvio.
Il budget di capture/fattura non si azzera con un nuovo MessageId.

La ricevuta e il lavoro sopravvivono anche prima dell'ack. Se Payments ha già
addebitato ma Orders non ha ancora CaptureId, il riavvio ripete la capture con
AuthorizationId e recupera lo stesso risultato. Se lo stato è Captured, riprende
solo la fattura. Se anche la fattura è riuscita prima di un timeout o di un rollback
locale, la ripetizione con CaptureId recupera lo stesso InvoiceId.

Al riavvio si riprendono soltanto i passi con budget residuo e non bloccati;
un tentativo interrotto è già consumato. Invalidi o passi esauriti/bloccati ricevono
nack senza requeue verso `<Name>.errors.v1`, routing `shipment.confirmed.v1`, coda
`<Name>.orders.shipment-confirmation.v1.errors`. Nessun loop di reinvio dalla DLQ.
Un errore SQL non gestibile interrompe la consegna: il broker conserva il messaggio
in DLQ; il lavoro eventualmente acquisito resta nel DB per diagnosi/ripresa.
Le riconnessioni del consumer sono limitate a tre fallimenti per esecuzione.
La readiness rimane quella infrastrutturale US02, non certifica il flusso business.

Dopo la correzione della causa, fermare la sola istanza/ambiente interessata e
aprire esplicitamente un nuovo ciclo nel solo DB Orders. Esempio capture:

```sql
UPDATE shipment_work SET capture_attempts=0, capture_blocked=0
WHERE order_id='<OrderId verificato>'
AND EXISTS(SELECT 1 FROM orders o WHERE o.order_id=shipment_work.order_id AND o.status='Shipped');
```

Per la fattura usare invoice_attempts/invoice_blocked e stato Captured. Per il
mapping correggere/verificare la causa, poi azzerare lookup_attempts, blocked e
last_error della sola ricevuta ancora bound=0/processed=0. Riavviare Orders: il
sorgente originale nel registro avvia la ripresa. L'eventuale reinvio operatore
dalla DLQ conserva corpo, MessageId e proprietà AMQP. Non cambiare WarehouseReference,
ShipmentId, AuthorizationId o CaptureId nelle righe per forzare una ripresa.
I messaggi conservati nella coda errori non sono eliminati automaticamente.

Query di consultazione con un client SQLite sul DB proprietario:

```sql
SELECT order_id,status,authorization_id,last_error FROM orders;
SELECT * FROM shipment_work;
SELECT message_id,order_id,bound,processed,lookup_attempts,blocked,last_error FROM shipment_inbox;
```

## Verifiche e confini

CompletionTests usa Kestrel e SQLite reali: concorrenza e duplicati dopo riapertura,
consultazione Checkout/Orders, sconosciuti e conflitti, mapping incoerente seguito
da conferma valida, 404 temporanei, rollback di binding/capture/fattura, timeout
dopo effetto remoto, blocco di 400/409 e risposte incoerenti, proprietà AMQP/UTF-8.
Le prove di rollback sono fault injection SQL e riapertura degli store, non kill
di processi dichiarati senza esecuzione.

CompletionBrokerTests usa factory/consumer Orders, Payments, RabbitMQ reali e un
server HTTP di mapping conforme ai contratti. Cinque scenari osservano ack/DLQ,
duplicati e ripartenza, sconosciuti/invalidi/conflitti, arresti effettivi dell'host
prima del commit locale di capture e dopo capture prima della fattura, timeout HTTP
reale di 5 secondi con effetto remoto e recupero operatore. Per gli arresti un
adattatore HTTP di test trattiene la risposta dopo la vera capture, oppure la
richiesta invoice prima del suo inoltro; al riavvio si usa direttamente Payments.
Quantità di effetti e ID finali sono verificati nei DB e tramite endpoint.

Ogni prova broker crea/rimuove un vhost us06-UUID, dati UUID e porte dinamiche,
senza consumare le code degli host attivi o di B. Comandi: restore locked, build
Release, test, poi start/ready/verify/stop/reset US02 su Name/PortBase isolati.
Gli skip offline non attestano le prove RabbitMQ. SHA, risultati e CI sono nella
issue/PR US06. La verifica con **tutti** i servizi reali resta US10, separata da US11.
Nessuna instrumentazione OpenTelemetry, exporter, Collector o dipendenza Aspire.
