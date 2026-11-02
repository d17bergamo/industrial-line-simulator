# Architettura e modello di simulazione

## 1. Moduli e dipendenze

```
Models ◄── Core ◄── Optimization ◄── Engine ◄── Infrastructure ◄── App (WPF)
            (Engine: SimulationEngine.cs = ciclo di scansione, SimulationEngine.Devices.cs = sequenze dei dispositivi)
                                         ▲                          
                                       Tests (Models, Core, Optimization, Engine, Infrastructure)
```

| Modulo | Responsabilità | Dipendenze esterne |
|---|---|---|
| **Models** | Classi di configurazione serializzabili (moduli di via a rulli con inverter, fotocellule, legatrici, stopper, trasferitore, pacchi, regole), attributi per il pannello proprietà, linguaggio testuale delle condizioni | nessuna |
| **Core** | Unità, cinematica (frenatura, profilo trapezoidale), formule teoriche, topologia (zona = modulo di via a rulli), validazione, progetti di esempio | nessuna |
| **Optimization** | Problema di legatura, ottimizzatore esatto (programmazione dinamica), strategia predefinita e manuale, confronto e motivazione | nessuna |
| **Engine** | Motore a tempo discreto, runtime dei componenti, regole, registrazione ciclogramma/fotogrammi, risultati | nessuna |
| **Infrastructure** | JSON (System.Text.Json) con versione dello schema e migrazioni, salvataggio atomico, autosave, export CSV/Excel | ClosedXML |
| **App** | WPF + MVVM, scena 3D, ciclogramma, dashboard | HelixToolkit.Wpf |

Il motore **non dipende dalla UI**: l'interfaccia legge solo `SimFrame` (stato istantaneo) e `SimulationRecorder` (storico). La posizione disegnata deriva sempre dallo stato del motore: non esistono animazioni indipendenti.

### Scelte tecnologiche
- **.NET 10 LTS + WPF**: piattaforma desktop Windows nativa, supporto lungo, pubblicazione self-contained a file singolo.
- **HelixToolkit.Wpf** (2.x): libreria open source matura sopra `Viewport3D` di WPF; usata solo per la telecamera (rotazione/zoom/pan, view cube). Le mesh sono generate dal progetto (`MeshFactory`), quindi la dipendenza è minima e sostituibile.
- **MVVM senza framework**: `ObservableObject`/`RelayCommand` interni, nessuna dipendenza aggiuntiva.
- **ClosedXML** per l'Excel; CSV con separatore `;` e numeri in formato italiano (apribili direttamente in Excel).
- **xUnit** per i test.

## 2. Modello fisico

- **Asse linea monodimensionale** `x` (m), verso positivo = flusso. Testa del pacco = estremità a valle; coda = testa − lunghezza.
- **Zona = modulo di via a rulli**: ogni modulo ha il proprio inverter (geometria e azionamento nello stesso componente). Schema v3: i progetti v1/v2 con inverter e sezioni separati vengono migrati fondendo le sezioni dello stesso inverter in un modulo con l'ID dell'inverter (i riferimenti restano validi).
- **Inverter del modulo**: riferimento di velocità (fermo / bassa / alta / posizionamento / allineamento) seguito con accelerazione e decelerazione costanti (rampe lineari).
- **Trascinamento del pacco**: velocità obiettivo = **minimo** delle velocità delle sezioni sotto il pacco (scelta conservativa: un rullo fermo frena il pacco). La variazione di velocità del pacco è limitata dalla *decelerazione massima d'attrito* (default 2,5 m/s²); oltre quel limite si registra uno **slittamento**.
- **Integrazione**: passo fisso (default 10 ms), posizione con regola trapezia sulla velocità ⇒ esatta per moti ad accelerazione costante.
- **Fotocellule**: stato grezzo dalla posizione reale dei pacchi; l'uscita commuta solo se lo stato grezzo resta stabile per il **tempo di risposta**.
- **Stopper**: battuta rigida; all'impatto si registra la velocità (avviso se oltre il massimo ammesso). Durante l'allineamento il pacco resta fermo in battuta mentre i rulli girano alla velocità di allineamento; durata = allineamento + intestatura.
- **Trasferitore**: sollevamento → traslazione → abbassamento → deposito (uscita del pacco) → ritorno. Con *Blocca ingresso finché non torna in posizione* la zona dello stopper non accetta pacchi finché il carro non è a casa; in caso contrario un pacco che entra nell'area durante il movimento genera una **collisione con il trasferitore**.

## 3. Ciclo di scansione (come un PLC)

1. Ingresso pacchi (intervallo + distanza minima libera in ingresso).
2. Fotocellule (tempo di risposta).
3. Prenotazione delle zone a pacco singolo (stazione di legatura, zona stopper).
4. Regole logiche e azioni delle fotocellule, con gli ingressi della scansione precedente; i segnali impostati diventano visibili alla scansione successiva.
5. Comandi delle zone **da valle a monte**:
   - stazione di legatura: posizionamento / arresto in legatura;
   - zona stopper: allineamento;
   - **accumulo**: una zona si ferma se una sua fotocellula di accumulo è occupata **e** la zona a valle non accetta quel pacco (zona a valle ferma, oppure zona a pacco singolo prenotata da un altro pacco);
   - **consegna a valle**: se un pacco a cavallo del confine è già stato accettato dalla zona a valle, la zona continua a marciare finché la consegna è completa, purché il pacco fermo in fotocellula possa ancora arrestarsi prima della fine zona (altrimenti si ferma e l'eventuale blocco viene segnalato);
   - interblocchi (arresto) con priorità massima; poi velocità bassa > alta > default.
6. Rampe degli inverter.
7. Moto dei pacchi da valle a monte: trascinamento, battuta stopper, contatto con il pacco a valle (**collisione** registrata e risolta senza compenetrazione), distanza minima di sicurezza, interferenza col trasferitore.
8. Macchine: posizionamento e legatura, legatrici, stopper, trasferitore.
9. Statistiche, ciclogramma, fotogrammi; rilevamento di fine, **deadlock** (nessun progresso per il timeout configurato) e durata massima.

Lo spazio di frenatura non è solo verificato in simulazione: il **validatore** controlla per ogni fotocellula di accumulo che `v·t_risposta + v²/(2d)` stia nella zona comandata (errore) e lasci la distanza di sicurezza (avviso).

### Posizionamento in legatura
Profilo di velocità calcolato sulla distanza residua `r`: `v = min(vmax, √(2·0,95·d·r), r/Δt)` (equivalente a un posizionatore con encoder). La prima fermata usa la velocità alta, le successive la velocità di posizionamento. Errore residuo < 0,2 mm; oltre la tolleranza viene registrato un errore.

### Logiche di processo (v0.2)

- **Fotocellule**: uscita ritardata del tempo di risposta; fronte di salita = *arrivo*, livello = *presenza*, fronte di discesa = *uscita completa/passaggio*. La fotocellula di ingresso (obbligatoria, controllata dal validatore e aggiunta automaticamente ai progetti v1) identifica il pacco e ne misura la lunghezza (testa − posizione fotocellula al fronte di discesa, quindi con l'errore dovuto al tempo di risposta). A livello di zona viene registrata l'uscita completa del pacco dalla sezione.
- **Arresto automatico**: nel ramo di marcia normale una zona con *arresto automatico* si ferma se non esiste un pacco nella zona, né un pacco con la testa entro la *distanza di preavviso* a monte (max tra lunghezza della sezione precedente e `v_monte · v_zona / a_zona + 0,5 m`), né (prima zona) un ingresso previsto entro il tempo di accelerazione. Una zona ferma per arresto automatico continua a dare il consenso all'ingresso (non blocca l'accumulo a monte).
- **Stazione di legatura**: macchina a stati `Posizionamento → Compattazione → Chiusura stringifascio → Legatura → Apertura → (fermata successiva | Rilascio)`. Ogni comando di chiusura richiede: pacco sotto il dispositivo, via a rulli e pacco fermi, consenso di avvio, condizione di arresto non attiva. Lo stato *chiuso* esiste solo dopo il segnale di conferma (tempo di movimento + ritardo); oltre il timeout il dispositivo va in guasto e dopo il tempo di ripristino ripete il comando. L'avvio delle legatrici verifica di nuovo lo stringifascio chiuso (interblocco). Lo stringifascio si apre solo quando tutte le legatrici della fermata hanno finito la fase di legatura e il mantenimento è scaduto; il pacco riparte solo con dispositivi aperti e teste tornate.
- **Ottimizzatore con dispositivi**: le posizioni ammesse della testa sono ristrette affinché il pacco copra stringifascio e compattatore (`H − L ≤ X ≤ H`); il tempo di fermata diventa `pre + max_i(prep_i + leg_i + max(rit_i, post))`, ottenuto con cicli "efficaci" per legatrice: l'algoritmo esatto resta invariato.
- **Stati KNCA 7/800** calcolati a ogni scansione dallo stato della stazione e della testa (disponibile, attesa pacco, posizionamento, pacco in posizione, attesa consenso stringifascio, ciclo di legatura, ciclo completato, scarico pacco, guasto). Il guasto simulato blocca la testa per il tempo di ripristino, poi il ciclo riparte.
- **Rigettatrice SMA**: il pacco marcato viene identificato all'ingresso; il ciclo si abilita, il pacco entra nella zona solo quando è libera da altri pacchi (zona comandata da un unico inverter), viene posizionato al centro del campo di spinta (profilo a encoder), espulso lateralmente (conferma con ritardo/timeout), poi spintore in ritorno e consenso alla ripartenza. Durante il ciclo la zona non accetta altri pacchi.
- **Fermi intermedi**: la zona dello stopper diventa multi-pacco. Assegnazione in ordine (stopper finale, poi fermi da valle a monte) solo se il pacco sta sul trasferitore e non si sovrappone al pacco del fermo a valle (distanza di sicurezza inclusa). Il pacco del fermo viene portato con posizionamento a encoder: intestatura di **testa** = battuta sul fermo sollevato con rulli in allineamento; intestatura di **coda** = coda sul riferimento e pacco trattenuto (il flusso inverso non è modellato). Il trasferitore parte quando tutti i pacchi assegnati sono allineati e il gruppo è completo, oppure il pacco successivo non può entrare, oppure è scaduta l'attesa massima.
- **Bilici**: alla fine della traslazione il trasferitore verifica che i bilici sotto i pacchi siano liberi (altrimenti attende); un pacco che copre più bilici viene pesato in modo combinato. Posizionamento (standard: testa a fine bilico; pacchi corti: centrato o posizione intermedia configurata), verifica posizione, stabilizzazione, acquisizione (risoluzione), consenso a proseguire, scarico. L'uscita del pacco (tempo ciclo) coincide con lo scarico dal bilico.

## 4. Ottimizzazione delle legature

Per una legatrice in posizione `X` e una legatura a distanza `d` dalla testa, il pacco deve fermarsi con la testa in `H = X + d`. Le posizioni ammesse sono quelle con il pacco interamente nella zona di legatura. Più legature con la stessa `H` possono essere eseguite **in parallelo** (tempo = ciclo più lungo).

Per **ogni combinazione** di legatrici abilitate (singole, coppie, terna) un algoritmo di **programmazione dinamica esatta** sullo stato *(fermata candidata, insieme legature eseguite, insieme legatrici usate)* trova la sequenza di fermate crescenti che copre tutte le legature con tempo minimo:

`tempo = Σ spostamenti (profilo trapezoidale) + Σ max(cicli in parallelo alla fermata)`

Vincoli: avanzamento solo in avanti, una legatura per legatrice per fermata, legatrici non combinabili escluse dalle combinazioni, posizioni nel campo ammesso, ingombri non interferenti (validatore), distanze minime delle legature dal bordo e tra loro (validatore). Pareggi risolti in modo deterministico (meno fermate, meno legatrici, meno tempo-macchina). Un test confronta l'ottimo con una **ricerca esaustiva** su 300 istanze casuali a seme fisso.

Strategie confrontate: **automatica** (ottimo), **predefinita** (una legatura per fermata con la prima legatrice che la raggiunge) e **manuale** (piano dell'utente, verificato). La scheda *Strategie di legatura* riporta tutte le combinazioni e il motivo della scelta.

## 5. Risultati

- Tempo ciclo = intervallo tra uscite consecutive; produttività **effettiva** = 3600 / tempo ciclo medio (a regime) e media sull'intera prova.
- Produttività **teorica** separata: `3600·v/L` (senza spaziatura) e `t/h = pacchi/h · peso / 1000`.
- Per ogni risorsa: occupazione, utilizzo, tempo morto, tempo bloccato da valle, impegno per pacco.
- **Collo di bottiglia** = risorsa con maggiore impegno per pacco (escluso il tempo bloccato), confrontata con l'intervallo di alimentazione; con spiegazione.
- Anomalie: collisioni, violazioni della distanza minima, slittamenti, impatti eccessivi, errori di posizionamento, zone sovraffollate, pacchi non legati, deadlock.

## 6. Persistenza

JSON camelCase con `schemaVersion`; condizioni polimorfiche con discriminatore `$type`. File più recenti del programma vengono rifiutati; il punto di estensione `ProjectSerializer.Migrate` gestisce gli aggiornamenti futuri. Salvataggio atomico (file temporaneo + sostituzione), autosave ogni 2 minuti in `%LOCALAPPDATA%\IndustrialLineSimulator\Autosave`.

## 7. Limiti noti (v0.2)

- Flusso solo in avanti (sezioni in direzione inversa segnalate come errore dal validatore).
- La stazione di legatura usa **un** modulo (un inverter) e lavora **un pacco alla volta**; non è modellato il lavoro contemporaneo di legatrici diverse su pacchi diversi.
- La legatrice resta sul pacco fino a fine ritorno (il pacco riparte dopo il ritorno: scelta conservativa).
- Stopper sempre sollevato (il transito a stopper abbassato non è modellato).
- La geometria laterale (larghezza pacco, distanza di rilevamento) è grafica: il modello di rilevamento è monodimensionale lungo la linea.
- L'avvio del trasferitore è comandato dalla logica integrata (pacco allineato + carro a casa); le regole agiscono sugli inverter dei moduli.
- Interfaccia: l'editor delle condizioni è testuale; non c'è annullamento (undo).
- Intestatura di coda: simulata come posizionamento a encoder con coda sul riferimento e pacco trattenuto; la spinta all'indietro contro un fermo posteriore non è modellata.
- I fermi intermedi esistono solo nella zona dello stopper; altrove si usano fotocellule e regole.
- La rigettatrice espelle un pacco alla volta, con il pacco completamente nel campo di spinta; i pacchi da rigettare a valle della stazione di legatura vengono legati prima del rigetto.
- Pesatura combinata su più bilici senza movimento di posizionamento (i rulli dei bilici non sono sincronizzati).
- Guasti simulati deterministici (al ciclo n) con ripristino automatico dopo il tempo impostato; non ci sono guasti casuali.
