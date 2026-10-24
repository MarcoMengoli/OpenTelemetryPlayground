# Inventory — US05

Inventory.Service consuma `ReserveInventory.v1` dalla coda
`<Name>.inventory.reserve.v1` della topologia US02. Nessuna nuova route HTTP,
risposta business o attesa da parte di Fulfillment. La disponibilità è garantita:
`reserved` è un totale prenotato, senza confronto con una giacenza disponibile.

## Persistenza e identità

Il solo database proprietario è `.local/<Name>/data/Inventory.Service/state.db`.
`InventoryStore` mantiene:

- `inventory_work`: OrderId, righe originali canoniche, messaggio sorgente,
  completamento, tentativi e ultimo errore operativo;
- `inventory_inbox`: MessageId univoco, corpo canonico e registrazione elaborata;
- `inventory_reservations`: chiave primaria `(order_id, sku)`, quantità positiva
  e prezzo originale;
- `inventory_stock`: chiave SKU e totale `reserved`, intero SQLite a 64 bit.

L'acquisizione verifica sia MessageId sia l'intero contenuto business di OrderId.
Righe riordinate e prezzi decimalmente equivalenti sono retry identici. Un nuovo
MessageId con le stesse righe è un duplicato logico; quantità, SKU o prezzi diversi
per l'ordine conosciuto sono conflitti. Lo stesso MessageId con ordine, corpo o
istante business diverso è un conflitto. Le chiavi non vengono rigenerate nei retry.

Il lavoro pendente e la ricevuta inbox vengono prima registrati atomicamente.
Ogni tentativo è poi consumato e committato prima dell'effetto. La transazione
IMMEDIATE di prenotazione inserisce tutte le righe, incrementa i totali con
`ON CONFLICT ... reserved = reserved + excluded.reserved`, completa il lavoro e
marca le ricevute inbox elaborate. Un errore su una riga annulla tutti questi
effetti; resta soltanto il lavoro pendente con il tentativo già consumato.
I vincoli e la transazione proteggono anche gli aggiornamenti concorrenti.

Una sola istanza Inventory per ambiente, come previsto dalla base. Il processor
serializza le chiamate della propria istanza, coerentemente con prefetch 1, per
evitare che duplicati concorrenti consumino il budget l'uno dell'altro. Non sono
introdotti leasing distribuiti o supporto a più istanze del servizio.

## Ack, errori e ripresa

Il consumer valida UTF-8, JSON v1, righe, proprietà AMQP e corrispondenza di
MessageId. Usa `autoAck=false`, prefetch 1 e ack soltanto dopo commit. Una
riconsegna già committata viene riconosciuta senza incrementare le quantità.
Un arresto nel confine commit/ack lascia il messaggio non confermato: RabbitMQ
lo riconsegna e il registro evita il secondo effetto.

Errori simulati e SQLite temporaneamente occupato/non accessibile hanno retry
limitati. Il budget business è di tre tentativi durevoli, con pause 1/2/4 secondi;
`reservation_temporarily_unavailable` e `reservation_retry_exhausted` sono errori
operativi, senza inventare un nuovo stato ordine. Se il DB non permette neppure
di persistere l'acquisizione o il contatore, sono comunque consentiti al massimo
tre tentativi locali di accesso: l'impossibilità di scrittura è riportata nei log.
Errori SQL non temporanei, input invalidi o conflitti non vengono ritentati.

Invalidi ed esauriti ricevono nack senza requeue: exchange `<Name>.errors.v1`,
routing `inventory.reserve.v1`, coda `<Name>.inventory.reserve.v1.errors`.
Nessun reinvio automatico dalla coda errori. Anche le riconnessioni del consumer
sono limitate a tre fallimenti per esecuzione, senza auto-recovery illimitata.
La readiness US02 certifica l'infrastruttura, non lo stato del consumer o del flusso.

Al riavvio vengono ripresi i lavori incompleti con budget residuo. Un tentativo
interrotto è già consumato; il riavvio o un nuovo MessageId non azzerano il budget.
Esaurimento e lavoro pendente restano consultabili. Per un nuovo ciclo, correggere
prima la causa (compresa la configurazione simulata), fermare l'istanza del solo
ambiente scelto e usare un client SQLite sul suo DB:

```sql
UPDATE inventory_work SET attempts = 0, last_error = NULL
WHERE order_id = '<OrderId verificato>' AND completed = 0;
```

Riavviare Inventory: il sorgente originale viene riletto dal registro e la
prenotazione ripresa. Un eventuale reinvio manuale del messaggio dalla sola coda
errori di questo ambiente conserva corpo, MessageId e proprietà AMQP originali;
dopo il recupero è un duplicato senza effetti. Il registro non elimina le prove
conservate nella coda errori. Non modificare i DB degli altri servizi.

## Consultazione e scenari deterministici

Con un client SQLite, ad esempio `sqlite3 -readonly` se disponibile, eseguire sul
database Inventory del proprio ambiente:

```sql
SELECT sku, reserved FROM inventory_stock ORDER BY sku;
SELECT order_id, sku, quantity, unit_price FROM inventory_reservations ORDER BY order_id, sku;
SELECT order_id, completed, attempts, last_error FROM inventory_work ORDER BY rowid;
SELECT message_id, order_id, processed FROM inventory_inbox ORDER BY rowid;
```

I log essenziali ILogger/Serilog indicano OrderId, tentativo ed errore. Non serve
aggiungere diagnostica ai messaggi o nuovi endpoint ai contratti condivisi.
Nessuna instrumentazione OpenTelemetry, exporter, Collector o dipendenza Aspire.

Configurazione del **solo processo Inventory**, tramite argomenti .NET o variabili
`Simulation__...`, insieme alle normali impostazioni Lab US02:

| Chiave | Valore e comportamento |
| --- | --- |
| `Simulation:Profile` | `happy-path` (default) oppure `temporary-inventory-failure` |
| `Simulation:FailuresBeforeSuccess` | 0–1000; nel profilo temporaneo falliscono i primi N tentativi durevoli per OrderId |
| `Simulation:OrderId` | UUID non vuoto; omesso seleziona tutti gli ordini |
| `Simulation:DelayMs` | 0–60000, default 0; attesa dopo il commit e prima dell'ack per l'ordine selezionato |

Due errori temporanei portano a successo al terzo tentativo, anche attraversando
un arresto. Tre errori esauriscono il ciclo: cambiare la causa e aprire il ciclo
operatore prima della ripresa. Il ritardo non mantiene transazioni SQLite aperte.
I parametri si applicano al processo Inventory; non propagare il profilo agli
altri host, che possiedono simulazioni differenti. Non avviare una seconda copia
sullo stesso DB/porta per modificare una simulazione già in esecuzione.

## Prove

`InventoryTests` usa SQLite reale e directory UUID: duplicati concorrenti,
aggiornamenti concorrenti di ordini distinti sullo stesso SKU, payload incompatibili,
rollback su seconda riga mediante trigger di test, riapertura/recupero con contatori
persistenti, budget esaurito e ciclo operatore, cancellazione dopo commit, quantità
invalide, proprietà AMQP e UTF-8 invalidi e selezione del profilo.

`InventoryBrokerTests`, abilitato da `lab.ps1 verify` come gli altri test Broker,
usa la factory e il consumer reali. Ogni prova crea/rimuove un vhost `us05-UUID`,
database/cartelle UUID e porte HTTP dinamiche. Non consuma le code del laboratorio
o del percorso B. Verifica quantità finali e ack reali, pubblicazioni concorrenti e
duplicati, dead-letter con avanzamento della coda, arresto effettivo dell'host prima
del commit durante una pausa di retry, arresto dopo commit/prima dell'ack con
redelivery osservata, esaurimento e recupero esplicito. Le risorse sono rimosse a
fine prova; non viene dichiarato un kill del processo che non è stato eseguito.

Comandi della base: `dotnet restore --locked-mode`, `dotnet build --no-restore -c Release`,
`dotnet test --no-build -c Release`, poi start/ready/verify/stop/reset con Name/PortBase
isolati. Il comando offline salta i test RabbitMQ: non certifica quelle prove.
I risultati con SHA e link CI sono registrati nella issue/PR US05. Il flusso con tutti
i servizi reali resta in US10, i guasti complessivi in US11 in ambiente separato.
