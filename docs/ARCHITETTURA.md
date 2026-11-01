# Architettura e modello di simulazione

## 1. Moduli e dipendenze

```
Models ◄── Core ◄── Optimization ◄── Engine ◄── Infrastructure ◄── App (WPF)
                                         ▲                          
                                       Tests (Models, Core, Optimization, Engine, Infrastructure)
```

| Modulo | Responsabilità | Dipendenze esterne |
|---|---|---|
| **Models** | Classi di configurazione serializzabili (sezioni, inverter, fotocellule, legatrici, stopper, trasferitore, pacchi, regole), attributi per il pannello proprietà, linguaggio testuale delle condizioni | nessuna |
| **Core** | Unità, cinematica (frenatura, profilo trapezoidale), formule teoriche, topologia (zone = inverter), validazione, progetti di esempio | nessuna |
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
- **Zona = inverter**: tutte le sezioni comandate dallo stesso inverter (devono essere contigue).
- **Inverter**: riferimento di velocità (fermo / bassa / alta / posizionamento / allineamento) seguito con accelerazione e decelerazione costanti (rampe lineari).
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

## 7. Limiti noti (v0.1)

- Flusso solo in avanti (sezioni in direzione inversa segnalate come errore dal validatore).
- La stazione di legatura usa **un** inverter e lavora **un pacco alla volta**; non è modellato il lavoro contemporaneo di legatrici diverse su pacchi diversi.
- La legatrice resta sul pacco fino a fine ritorno (il pacco riparte dopo il ritorno: scelta conservativa).
- Stopper sempre sollevato (il transito a stopper abbassato non è modellato).
- La geometria laterale (larghezza pacco, distanza di rilevamento) è grafica: il modello di rilevamento è monodimensionale lungo la linea.
- L'avvio del trasferitore è comandato dalla logica integrata (pacco allineato + carro a casa); le regole agiscono sugli inverter.
- Interfaccia: l'editor delle condizioni è testuale; non c'è annullamento (undo).
