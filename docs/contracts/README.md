# Contratti v1 — riferimento per US03–US12

## Forma verificabile

La fonte eseguibile è `src/Playground.Contracts/Contracts.cs`: record C# pubblici, `Validate()`
e `WireJson` sono l'equivalente verificabile dello schema. Ogni record ha un esempio
`examples/<NomeTipo>.valid.json`; i test scoprono tutti i record IContract e ne verificano
lettura, validazione e roundtrip. Ogni esempio `*.invalid*.json` deve essere rifiutato.
I test rifiutano inoltre `{}`, `null` e campi aggiuntivi per ciascun tipo, inclusi i file.
Non mantenere un secondo modello DTO divergente nei servizi.

JSON UTF-8, proprietà camelCase esatte, enum stringa con nomi dichiarati, date ISO-8601 con offset,
UUID non vuoti. Tutti i parametri dei record devono essere presenti, anche i nullable (valore null).
Campi sconosciuti e valori null sui campi non-nullable sono invalidi. Cifre monetarie decimali,
positive, massimo due decimali; solo EUR nella versione iniziale. Ogni SKU è non vuoto e unico
nelle righe di un ordine; quantità intere positive. Prezzi nel checkout sono un input simulato,
non c'è catalogo/prezzario sicuro di produzione. Totale = somma quantity * unitPrice.
Indirizzo: recipient, street, city, postalCode non vuoti; countryCode due lettere maiuscole.

Gli host US02 espongono le route sotto con **501 ApiError**, indipendentemente dal payload:
validazione e comportamento business saranno implementati nelle story indicate.
`GET /health/ready` su tutti gli host restituisce 200 `{service,status:"ready",scope:"foundation"}`
o 503 ApiError. Avvio incompleto non apre la porta HTTP.

## HTTP previsto (Content-Type application/json)

| Host / story | Metodo e route | Richiesta → risposta | Esito business |
|---|---|---|---|
| Checkout / US04 | POST `/api/v1/orders` | CheckoutRequest → OrderAccepted | 202 nuovo/ripetizione identica, Location `/api/v1/orders/{OrderId}` |
| Checkout / US04 | GET `/api/v1/orders/{orderId}` | UUID path → OrderSnapshot | 200; 404 sconosciuto |
| Orders / US04 | GET `/internal/v1/orders/{orderId}` | UUID path → OrderSnapshot | 200; 404 non ancora acquisito |
| Payments / US03 | POST `/api/v1/authorizations` | AuthorizePaymentRequest → AuthorizationResponse | 200 Authorized oppure Declined |
| Payments / US03 | POST `/api/v1/captures` | CapturePaymentRequest → CapturePaymentResponse | 200, addebito simulato con CaptureId |
| Couriers / US07 | POST `/api/v1/estimates` | DeliveryEstimateRequest → DeliveryEstimateResponse | 200 stima positiva |
| Fulfillment / US07 | GET `/internal/v1/warehouse-requests/{warehouseReference}` | riferimento URL-encoded → WarehouseMappingResponse | 200; 404 mapping sconosciuto |

Checkout conserva l'accettazione e delega la lettura a Orders. Nell'intervallo prima del consumo
OrderCreated, restituisce il proprio snapshot Created (campi opzionali null), non un falso 404.
404 client solo quando né Checkout né Orders conoscono OrderId. Se Orders è irraggiungibile,
503 invece di uno snapshot vecchio presentato come attuale (eccetto accettazione non ancora inoltrata).
Le chiamate GET interne sono locali, senza accesso ai database altrui.

Errore uniforme `ApiError {code,message}`: 400 `invalid_request`, 404 `not_found`,
409 `idempotency_conflict` (chiave business riutilizzata con payload diverso) o `invalid_state`
(addebito senza autorizzazione valida), 503 `temporarily_unavailable`, 500 `internal_error`.
US02 usa 501 `not_implemented`. I messaggi non devono contenere segreti o stack trace.
Un pagamento rifiutato è **200 + Declined**, con declineCode e authorizationId null;
un timeout/503 non è un rifiuto business. Retry limitati solo su errori temporanei/rete.
Non ritentare automaticamente 400/409; 404 mapping magazzino può essere ritentato con limite
per ordine di arrivo/ripartenza, poi va in quarantena. Il mapping deve precedere la pubblicazione file.

## Identità e stati

- **OrderId**: UUID generato dal client prima del primo POST, stabile su tutti i retry.
- **MessageId**: UUID generato dal produttore quando scrive l'outbox, immutato nelle ripubblicazioni.
  Un nuovo messaggio conseguente usa un nuovo MessageId. È anche la proprietà AMQP message_id.
- **AuthorizationId**, **InvoiceId**: UUID generati e persistiti da Payments; restituiti identici sui retry.
- **WarehouseReference**: stringa opaca non vuota generata da Fulfillment, univoca nel suo DB,
  stabile per OrderId e per rigenerazione del file. Non usare direttamente input esterni come pathname.
- **ShipmentId**: UUID generato e persistito dal simulatore per WarehouseReference, stabile sui duplicati.
- **OccurredAt**, **ShippedAt**: istanti business originali, non rigenerati sui retry.

| Da | Evento/esito | A |
|---|---|---|
| nessuno | OrderCreated | Created |
| Created | Authorized | PaymentAuthorized |
| Created | Declined | PaymentDeclined (terminale) |
| PaymentAuthorized | ReserveInventory + FulfillOrder salvati in outbox | FulfillmentPending |
| FulfillmentPending | ShipmentConfirmation valida | Shipped |
| Shipped | capture riuscita, CaptureId salvato | Captured |
| Captured | fattura riuscita, InvoiceId salvato | Completed (terminale) |

Snapshot: failureCode soltanto per PaymentDeclined; authorizationId richiesto da PaymentAuthorized
in poi; shipmentId richiesto da Shipped; captureId richiesto da Captured; invoiceId richiesto per Completed. I fallimenti temporanei
non inventano stati terminali: persistere tentativo/errore operativo separatamente e riprendere.
Autorizzazione = promessa simulata di pagamento; capture = addebito simulato; InvoiceId = ricevuta
simulata, nessuna conformità fiscale. Disponibilità garantita: ReserveInventory e FulfillOrder
partono in parallelo; non attendere un evento InventoryReserved e non aggiungerlo alla base.

## RabbitMQ

Vhost `<Name>`. Exchange direct durevole `<Name>.business.v1`, code durevoli, non esclusive,
non auto-delete. Prefisso di coda `<Name>.`. Topologia dichiarata da `Broker.DeclareAsync`.

| Tipo body | Routing key | Suffisso coda | Produttore → consumatore |
|---|---|---|---|
| OrderCreated | order.created.v1 | orders.order-created.v1 | Checkout → Orders |
| ReserveInventory | inventory.reserve.v1 | inventory.reserve.v1 | Orders → Inventory |
| FulfillOrder | fulfillment.fulfill.v1 | fulfillment.fulfill.v1 | Orders → Fulfillment |
| ShipmentConfirmation | shipment.confirmed.v1 | orders.shipment-confirmation.v1 | WarehouseResponse → Orders |

Ogni body contiene schemaVersion=1, messageId, occurredAt, orderId e i campi business del relativo DTO.
AMQP: content_type application/json, delivery_mode persistent, message_id come body, type `<Tipo>.v1`.
Nessun header diagnostico aggiunto dall'applicazione. Modifiche incompatibili richiedono v2 e
migrazione esplicita di producer/consumer. Campi ignoti non sono un'estensione compatibile di v1.

Regole da implementare nelle story applicative:

1. Publisher con `mandatory=true` e publisher confirmation tracking. Return/nack/disconnessione
   lasciano il record outbox pendente. Segnare inviato solo dopo confirm; non basta scrivere sul socket.
2. Consumer `autoAck=false`, prefetch iniziale 1. Validare tipo/versione/identità, poi transazione
   inbox + stato + outbox. Ack dopo commit. Duplicato già committato: ack senza ripetere effetti.
3. Errori transitori: massimo 3 tentativi per ciclo con pause 1s/2s/4s e contatore durabile nella
   story proprietaria. Mai requeue illimitato in un loop caldo. Per HTTP usare timeout 5s e
   la stessa chiave idempotente; un esito incerto può aver già prodotto l'effetto remoto.
4. Invalidi o tentativi esauriti: nack senza requeue verso `<Name>.errors.v1`, stessa routing key,
   coda `<coda-originale>.errors`. Queste risorse sono già dichiarate. Un eventuale reinvio operatore
   conserva MessageId e richiede correzione della causa; niente retry automatico dalla coda errori.
5. Outbox conserva messaggi non inviati anche dopo budget esaurito; segnala fallimento operativo
   e consente riavvio/ripresa esplicita. I dettagli del dispatcher restano nella story proprietaria.

La prova US02 usa risorse UUID separate: confirm, routing, restituzione obbligatoria, nack/requeue,
redelivery e ack reali. Non afferma che inbox/outbox o retry business siano già implementati.

## File magazzino

**WarehouseRequest**: schemaVersion, warehouseReference, items, shippingAddress, courierCode,
estimatedDays. **WarehouseResponse**: schemaVersion, warehouseReference, shipmentId, shippedAt.
Gli esempi validi/invalidi sono nella cartella examples. Nessun OrderId è necessario nel file:
WarehouseReference permette il lookup affidabile. Non aggiungere traceId/spanId/baggage o campi
diagnostici ai file o al mapping. Il lookup risponde solo con WarehouseReference e OrderId.

Protocollo da implementare in US08/US09:

- Fulfillment persiste mapping e file-outbox **prima** del file richiesta. Simulator e
  WarehouseResponse possono elaborare solo `*.json` completati nelle rispettive directory.
- Scrivere UTF-8 in `<nome>.tmp-<uuid>` nella **stessa directory** del nome finale; flush/chiusura,
  poi rename atomico senza overwrite. Ignorare i temporanei durante il polling (default 500ms).
  Nomi finali derivati da identificativi generati e sanificati, mai da path nel payload.
- Dopo crash, rigenerare dall'outbox non completata; un `.json` già presente con contenuto identico
  è successo idempotente, diverso è conflitto. I temporanei orfani non sono richieste: rimuoverli
  solo quando sicuramente non appartenenti a uno scrittore attivo (singola istanza locale).
- Persistire inbox/risultato/outbox prima di archiviare input in `processed/requests` oppure
  `processed/responses`. Crash prima dell'archivio causa rilettura e deduplicazione. Le sottocartelle
  sono create dalla story di polling; US02 predispone le quattro radici.
- JSON invalido, versione ignota, chiavi mancanti, conflitti o mapping irrisolvibile dopo tentativi
  limitati vanno in `errors/requests` o `errors/responses`, con nomi unici per non sovrascrivere prove.
  Motivo nei log strutturati, non aggiunto ai payload JSON. Una IOException temporanea mantiene il
  file per retry limitati; impossibilità di quarantena produce un errore evidente, non cancellazione.
- Risposta duplicata identica non crea nuova spedizione o nuovo addebito. Risposta con riferimento
  sconosciuto non può inventare OrderId. Nessun accesso diretto al DB Fulfillment.

I profili deterministici futuri sono descritti nell'architettura e si configurano fuori dai file.

## Operazioni Payments e recupero Orders (US03 e US06)

Capture e fattura sono **due operazioni pubbliche distinte**, così è verificabile il recupero
quando Orders si arresta dopo l'addebito e prima della fattura. CapturePaymentResponse contiene
CaptureId, non InvoiceId. Si aggiungono i seguenti contratti già validati nella base:

| Host | Metodo e route | Richiesta → risposta | Esito |
|---|---|---|---|
| Payments | POST `/api/v1/authorization-cancellations` | CancelAuthorizationRequest → CancelAuthorizationResponse | 200 Cancelled |
| Payments | POST `/api/v1/invoices` | IssueInvoiceRequest → InvoiceResponse | 200 fattura simulata |
| Payments | GET `/api/v1/payments/{orderId}` | UUID path → PaymentSnapshot | 200; 404 sconosciuto |

CaptureId è un UUID generato e persistito da Payments, univoco per AuthorizationId, immutato sui retry.
Chiavi per operazione: autorizzazione OrderId; cancellation e capture AuthorizationId (scope di
operazione distinto); fattura CaptureId. Payload incompatibile con la chiave: 409. La fattura usa
l'importo dell'addebito persistito; non accetta un nuovo importo dal chiamante.

Stati Payments: Authorized → Captured → Invoiced; Authorized → Cancelled; Declined terminale.
Cancellation ripetuta restituisce Cancelled; dopo Captured/Invoiced è 409 invalid_state.
Capture dopo Cancelled/Declined è 409; invoice prima di Captured è 409. Un nuovo tentativo di
Authorize per OrderId già conosciuto restituisce l'esito originale senza ricreare autorizzazioni.
PaymentSnapshot mostra separatamente AuthorizationId, CaptureId, InvoiceId, Total e DeclineCode.

Si precisa la transizione Orders: Shipped → **Captured** dopo capture persistita, poi Captured →
Completed soltanto dopo InvoiceId persistito. OrderSnapshot include CaptureId (obbligatorio da
Captured), LastError nullable con codice operativo dell'ultimo errore temporaneo/retry esaurito,
e FailureCode riservato al rifiuto business. Un errore lascia lo stato business al passo raggiunto;
il lavoro pendente è persistito, visibile e riprendibile. Completed richiede LastError null.
Al riavvio Orders ripete capture con AuthorizationId se l'esito locale era ambiguo, recupera lo
stesso CaptureId, poi fattura con CaptureId. Nessuna nuova identità per aggirare un timeout.
Impatto: US03 implementa le tre route aggiuntive; US06 osserva Captured e recupera fatturazione;
il percorso Fulfillment e i contratti file/messaggi restano invariati.
