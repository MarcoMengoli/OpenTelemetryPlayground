# Checkout e Orders — US04

Questa story copre l'accettazione e l'avvio dell'ordine fino a ReserveInventory e
FulfillOrder. Inventory/Fulfillment non devono essere già implementati; le prove
del percorso osservano i loro comandi con consumer di test. La conferma spedizione,
capture e fattura in Orders appartengono a US06. Nessuna strumentazione OTel/Aspire.

## HTTP e consultazione

POST /api/v1/orders su Checkout usa CheckoutRequest v1. OrderId è creato dal client
prima del primo invio. Solo dopo il commit di accettazione e outbox la risposta è
202 OrderAccepted, con Location /api/v1/orders/{OrderId}. Il retry restituisce 202
anche se il processo è già avanzato: è una ricevuta di accettazione, non lo stato
corrente. Stesso OrderId con dati incompatibili produce 409 idempotency_conflict.
SKU vengono ordinati e la scala dei prezzi normalizzata: riordinare righe identiche
o scrivere 12.5 invece di 12.50 non crea un secondo ordine. Quantità, prezzi,
indirizzo e insieme degli SKU devono corrispondere. Input invalido o totale decimale
non rappresentabile è 400 invalid_request e non crea né accettazione né messaggi.

GET /api/v1/orders/{orderId} interroga via HTTP GET /internal/v1/orders/{orderId}
di Orders. Se Orders risponde 404 ma Checkout conosce l'accettazione, restituisce
Created con gli ID successivi null: il consumo asincrono può non essere avvenuto.
404 client significa che nessuno dei due conosce l'ordine. Un Orders irraggiungibile
produce 503, salvo un'accettazione il cui publish non è **mai stato tentato**.
Il marcatore publish_started sopravvive ai riavvii: un confirm ambiguo non autorizza
a presentare uno snapshot Created come attuale. Nessun accesso al database altrui.

I default degli indirizzi seguono le porte US02: Orders su Lab:RabbitPort+11 e
Payments su Lab:RabbitPort+12. Per avvio manuale/debug si possono impostare, sul
solo servizio chiamante, Services:OrdersBaseUrl e Services:PaymentsBaseUrl
(variabili Services__OrdersBaseUrl / Services__PaymentsBaseUrl). Le route e i DTO
non cambiano. Tutte le chiamate HTTP hanno timeout 5 secondi.

## Commit, stati e identità

CheckoutStore possiede il database Checkout.Api; Accept salva nella stessa
transazione IMMEDIATE l'input canonico e OrderCreated con MessageId/OccurredAt
generati una volta. Un crash dopo il commit lascia un messaggio da pubblicare.

OrdersStore possiede il database Orders.Service. All'arrivo di OrderCreated valida
JSON UTF-8, DTO/versione, Type, MessageId e proprietà AMQP persistenti. Registra
inbox con processed=0 e ordine Created nella stessa transazione. Questa è una
ricezione durevole, ancora incompleta: non permette di saltare l'autorizzazione.
Il riuso di MessageId con contenuto diverso o OrderId con input incompatibile è
un conflitto, da inviare alla coda errori. Un duplicato logico con un nuovo MessageId
non crea un secondo processo; l'ordine resta unico e l'inbox registra la nuova consegna.

OrderIntake chiama Payments con OrderId e totale originali. Dopo Authorized, un
unico commit salva AuthorizationId, attraversa PaymentAuthorized e lascia lo stato
FulfillmentPending, crea **due** record outbox con identità distinte e completa
l'inbox. La prenotazione non è un prerequisito dell'evasione: non si aspetta alcun
InventoryReserved. Il publisher invia i due comandi separatamente, senza dipendere
dagli esiti business dei destinatari. Un fallimento sul secondo non annulla né perde
il primo. Declined salva PaymentDeclined e failureCode, completa l'inbox e non crea
comandi. Un timeout non è un rifiuto: lascia Created con LastError operativo.

Non esiste una transazione distribuita con Payments. Se la risposta arriva tardi
o il commit locale fallisce, il retry recupera l'autorizzazione persistita attraverso
lo stesso OrderId; non viene inventata una nuova operazione per aggirare l'incertezza.

## Broker, retry e recupero

DurableOutbox contiene soltanto storage locale dei messaggi; ConfirmedPublisher
contiene il punto di invio RabbitMQ esplicito. Sono helper opzionali per questi due
servizi, senza cambiare i servizi B o imporre un nuovo bus. Ogni database ha la propria
outbox: MessageId unico, (OrderId, Type) unico, body/routing persistiti. Si usa
mandatory=true e publisher confirmation tracking; MarkSent avviene solo dopo il
confirm. Return, nack, perdita connessione e timeout mantengono il record non inviato.
Un confirm ricevuto seguito da crash prima di MarkSent può causare un duplicato con
la stessa identità/body: l'inbox di Orders lo gestisce senza nuovi effetti.

OrderCreatedConsumer usa autoAck=false e prefetch 1. Ack solo dopo elaborazione
committata; input invalido, conflitto o budget esaurito: nack senza requeue sulla
coda errori già definita da US02. Non legge automaticamente quella coda.

HTTP e outbox hanno al massimo tre tentativi per ciclo, contatori persistiti prima
del tentativo e pause 1/2/4 secondi dopo gli errori. Outbox: timeout 5 secondi per
connessione/publish/confirm, poi LastError publish_retry_exhausted e record conservato;
gli altri messaggi non sono eliminati. HTTP temporaneo/rete: LastError
payment_retry_exhausted e ordine ancora Created. HTTP 400/409 e risposte incompatibili
bloccano quell'autorizzazione senza retry automatico, neppure al riavvio. Errori
operativi usano LastError; failureCode resta riservato al rifiuto business.

Dopo correzione di un guasto temporaneo, fermare e riavviare **lo stesso** ambiente
con lab.ps1 stop/start, stessi Name/PortBase. Il riavvio apre un nuovo ciclo limitato
per le outbox non inviate e riprende da source_json le autorizzazioni Created non
bloccate. Anche se il messaggio originale è già nella coda errori, il lavoro locale
incompleto è conservato. Non rigenera MessageId, payload o autorizzazioni. I messaggi
già marcati inviati non vengono ripubblicati. Un errore permanente richiede prima
correggerne la causa e un intervento esplicito sul solo database Orders del laboratorio
per riabilitare authorization_blocked, poi stop/start; nessun endpoint o retry
automatico aggiuntivo. Il reset resta un'operazione distinta che elimina i dati del
solo ambiente scelto.

La connessione consumer ha tre riconnessioni/fallimenti per processo con pause
limitate, poi richiede riavvio; l'outbox si ferma dopo tre errori di accesso allo
storage e conserva il lavoro. La readiness mantiene scope foundation e certifica
l'infrastruttura; LastError e log dei worker rendono visibili gli errori operativi.

## Prove e isolamento

```text
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet test --no-build -c Release
pwsh ./scripts/lab.ps1 start -Name check-orders -PortBase 18300
pwsh ./scripts/lab.ps1 verify -Name check-orders -PortBase 18300
pwsh ./scripts/lab.ps1 stop -Name check-orders -PortBase 18300
pwsh ./scripts/lab.ps1 reset -Name check-orders -PortBase 18300
```

CheckoutOrderTests usa HTTP Kestrel e SQLite reali, database UUID, porte assegnate
dal sistema. Prova 202/idempotenza/conflitti, input invalidi, letture, rollback di
accettazione+outbox e di stato+due outbox tramite trigger SQL di test, duplicati,
timeout con Payments reale e ripresa. Solo il test locale di timeout usa un timeout
ridotto sul client di test; la configurazione del processo resta 5 secondi.

OrderBrokerTests è abilitato da PLAYGROUND_BROKER_TEST=1, come la prova RabbitMQ US02.
Il comando offline mostra esplicitamente questi test skipped. Lab verify li esegue
sul broker reale: crea un vhost us04-UUID e permessi tramite la management API locale
(stesse credenziali del laboratorio, porta broker+1), directory UUID e server HTTP
dinamici; elimina soltanto quel vhost e quei database al termine. Checkout e Orders
usano gli stessi factory/worker/lifecycle del processo reale; Payments usa gli stessi
handler/store HTTP della US03. Nessun sostituto del broker o del pagamento nelle prove
di percorso. Consumer di test osservano i comandi senza attivare Inventory/Fulfillment.

Copertura broker: percorso autorizzato e due comandi, rifiuto senza comandi, duplicati
di trasporto/logici, mandatory return, stop dopo il primo publish e recupero del
secondo dopo budget esaurito, timeout HTTP **reale di 5 secondi** dopo l'effetto,
quarantena e ripresa dal DB dopo riavvio, messaggi invalidi in coda errori. Non è
ancora la prova US10 con tutti i servizi reali del laboratorio.
