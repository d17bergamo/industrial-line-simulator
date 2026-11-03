# Industrial Line Simulator

Applicazione desktop Windows (C# / .NET 10 / WPF) per **disegnare, configurare e simulare** una linea di movimentazione di pacchi metallici: vie a rulli a moduli (ogni modulo con il proprio inverter), fotocellule e logiche di accumulo, stazione con 1–3 legatrici automatiche e ottimizzazione delle legature, stopper con allineamento, trasferitore, ciclogramma dinamico, produttività e report.

> ⚠️ **Strumento di progettazione e simulazione, non un sistema di sicurezza certificato.** Protezioni e interblocchi reali vanno verificati e implementati nel PLC e nei circuiti di sicurezza della macchina.

## Stato del progetto

| Area | Stato |
|---|---|
| Motore di simulazione (rampe, fotocellule con tempo di risposta, accumulo ZPA, anti-collisione, stopper, trasferitore, legatura, deadlock) | Implementato, coperto da test |
| Ottimizzazione legature (esatta, tutte le combinazioni) + confronto automatica/manuale/predefinita | Implementato, verificato contro ricerca esaustiva |
| Regole logiche AND/OR/NOT, segnali di consenso, azioni sulle fotocellule | Implementato, coperto da test |
| Risultati, collo di bottiglia, export CSV / Excel | Implementato, coperto da test |
| Progetti JSON versionati, autosave, duplicazione, esempi | Implementato, coperto da test |
| Logiche di processo: fotocellula di ingresso obbligatoria (identificazione), arrivo/presenza/uscita completa, arresto automatico degli inverter senza pacchi | Implementato, coperto da test |
| Legatrice a filo Sund Birsta KNCA 7/800 (carica filo 3 s, legatura 11,5 s) e reggettatrice Sund Birsta SMA (legatura 10 s, riarmo 5 s, sempre armata), stringifascio KNOF (2/2 s), compattatore laterale (3/3 s), con consensi, conferme, timeout e guasti simulati | Implementato, coperto da test |
| Fermi intermedi con intestatura di testa/coda, trasferitore multi-pacco, bilici di pesatura (pacchi corti in posizione intermedia, pesatura combinata), rigettatrice generica (scarto, disabilitata di default) | Implementato, coperto da test |
| Zone di comando (più moduli su un inverter), sensori di processo aggiunti in automatico (arresto davanti al trasferitore, encoder + fotocellula di riferimento) | Implementato, coperto da test |
| Output: logica di controllo AND/OR/NOT (JSON/CSV), specifica tecnica funzionale Word su richiesta, ottimizzazione del numero di inverter | Implementato, coperto da test |
| Interfaccia WPF: vista 3D (HelixToolkit), albero, proprietà, timeline, ciclogramma sincronizzato, dashboard | Implementata; da verificare a vista su Windows |
| CI GitHub Actions: build, test, EXE self-contained, ZIP, installer Inno Setup | Configurata |

**Verifica:** la GitHub Action su `windows-latest` compila l'intera soluzione (WPF compresa), esegue tutti i test automatici (motore, ottimizzatore, logiche di processo) e produce EXE self-contained, ZIP e installer. L'interfaccia grafica va ancora collaudata a vista su un PC Windows.

## Requisiti

- Per **usare** il programma: Windows 10/11 x64. L'EXE è *self-contained*: non serve installare .NET.
- Per **compilare**: .NET SDK 10 (`dotnet --version` ≥ 10.0.100), Windows per il progetto WPF. I progetti del motore e i test compilano anche su Linux/macOS.

## Installazione (utente finale)

1. Scaricare da *Actions → ultimo run → Artifacts* (o dalla pagina *Releases* per i tag `v*`):
   - `IndustrialLineSimulator-win-x64.zip` → estrarre e avviare `IndustrialLineSimulator.exe`, oppure
   - `IndustrialLineSimulator-Setup-x.y.z.exe` → installer con collegamenti nel menu Start.
2. All'avvio viene caricata la linea di esempio; altri esempi in *File → Esempi* (tra cui *Linea completa - KNCA, KNOF, compattatore, fermi, bilici*).
3. Se Windows SmartScreen blocca l'avvio: *Ulteriori informazioni → Esegui comunque* (l'EXE non è firmato digitalmente), oppure *Proprietà → Annulla blocco* sullo ZIP scaricato prima di estrarlo.

## Compilazione

```powershell
dotnet restore IndustrialLineSimulator.slnx
dotnet build IndustrialLineSimulator.slnx -c Release
dotnet test tests/LineSimulator.Tests/LineSimulator.Tests.csproj -c Release

# EXE singolo self-contained
dotnet publish src/LineSimulator.App/LineSimulator.App.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish/IndustrialLineSimulator
```

Solo motore e test (anche su Linux): `dotnet test IndustrialLineSimulator.Core.slnx`.

Installer: `ISCC.exe /DSourceDir=publish\IndustrialLineSimulator installer\LineSimulator.iss` (Inno Setup 6).

## Uso rapido

L'applicazione è una sola finestra con una **schermata iniziale** a tre riquadri; il pulsante *⌂ Home* (o il menu *Home*) riporta alla schermata iniziale. Le tre sezioni condividono lo stesso progetto e lo stesso motore di simulazione.

- **IMPOSTA LAYOUT**: progettazione geometrica. A sinistra struttura e proprietà del componente selezionato, al centro il modello 3D, a destra la **libreria macchine** (comprimibile con *Libreria ⇄* e ridimensionabile). Si trascina una macchina dalla libreria nell'area 3D (anteprima trasparente, punto di inserimento e coordinate); clic su un componente lo seleziona e trascinandolo lo si sposta lungo la linea; *Ruota 180°* porta reggiatrici, rigettatrice e trasferitore sul lato opposto; *Duplica* ed *Elimina* operano sul selezionato. La casella **MOSTRA DISTANZE DALL'ORIGINE** mostra, in metri, centro, ingresso e uscita di ogni macchina rispetto a X = 0. Layout e tipologie si salvano in file indipendenti (*File → Salva layout / Salva tipologie pacchi*).
- **CARATTERISTICHE PACCHI**: elenco delle tipologie (nome, lunghezza, peso, gestione sull'accumulo *Single/Double banking*, intestatura di testa o di coda, legature) con aggiunta, duplicazione ed eliminazione.
- **SIMULAZIONI**: tendina della tipologia da simulare (una sola per simulazione), vista 3D con comandi *Play / Pausa / Stop / Reset*, velocità x1/x2/x5/x10/x20/x100, stato delle apparecchiature, tempi di ciclo e produttività; scheda *Ciclogramma e risultati*.

Limiti attuali: *Single/Double banking* e l'intestatura della tipologia sono memorizzati ma non ancora usati dal motore (l'intestatura di testa/coda si imposta oggi sul fermo intermedio); stringifascio KNOF, compattatore e rigettatrice sono dispositivi singoli; il motore è lineare lungo l'asse X, quindi la rotazione è limitata a 180° (lato opposto) e il trascinamento è lungo la linea; le dimensioni dei modelli SUND BIRSTA sono segnaposto configurabili, non quote del costruttore.

Funzioni generali:

- **Albero della linea** (a sinistra): selezionare un componente per modificarne i parametri nel pannello **Proprietà**. Ogni modifica ricostruisce il modello 3D, rivalida il progetto, ricalcola le strategie di legatura e azzera la simulazione.
- **Vista 3D**: clic sinistro seleziona il componente; destro ruota; rotella zoom; viste *Alto / Laterale / Isometrica*.
- **Simulazione**: *Play* (velocità 1x/10x/50x/100x), *Pausa*, *Stop* (risultati parziali), *Reset*, *Passo* (un passo di integrazione), *Evento ▸* (fino al prossimo evento), *Fino al termine*.
- **Timeline e ciclogramma**: il cursore avanza con la simulazione; un clic sul ciclogramma o lo spostamento della timeline mostra nel 3D lo stato registrato in quell'istante. Rotella = zoom, Maiusc+rotella = scorrimento.
- **Strategie di legatura**: confronto tra combinazioni di legatrici, strategia predefinita e manuale, con motivazione della scelta.
- **Risultati**: tempo ciclo medio/min/max, pacchi/h e t/h effettivi (separati da quelli teorici), utilizzo risorse, tempi morti e blocchi, collisioni, collo di bottiglia. Export *File → Esporta CSV/Excel*.

### Moduli di via a rulli

Ogni **modulo di via a rulli** ha il proprio inverter: nel pannello proprietà del modulo si impostano insieme geometria (inizio, lunghezza, rulli) e azionamento (velocità alta/bassa, rampe, velocità di default, arresto automatico). Fotocellule, stazione di legatura e regole fanno riferimento all'ID del modulo. I progetti salvati con inverter e sezioni separati vengono convertiti all'apertura: le sezioni dello stesso inverter diventano un unico modulo con l'ID dell'inverter.

### Logiche di processo

- **Fotocellula di ingresso** (obbligatoria): ogni pacco viene identificato al fronte di salita (tipologia, peso, eventuale rigetto) e al fronte di discesa viene registrata la lunghezza rilevata. Ogni fotocellula ha una *logica di rilevamento*: presenza, impulso di arrivo, impulso di uscita completa.
- **Arresto automatico degli inverter**: l'inverter si spegne quando i suoi rulli hanno compiuto una corsa pari alla lunghezza della zona senza che le sue fotocellule rilevino pacchi (non si considerano il modulo precedente né i pacchi in arrivo). **Ripartenza** comandata dalla fotocellula a monte della zona quando rileva l'arrivo di un pacco. Disattivabile per modulo.
- **Zone di comando**: ogni modulo ha di default il proprio inverter; con il campo *Zona di comando / inverter* più moduli contigui possono essere comandati da un solo inverter (parametri del primo modulo). *Ottimizzazione → Ottimizza zone di comando* simula tutte le suddivisioni e propone quella con il minimo numero di inverter; a parità, la produttività maggiore su tutte le tipologie di pacco.
- **Sensori aggiunti in automatico** se mancano: fotocellula di arresto davanti al trasferitore sollevabile (distanza = 1,5 s × velocità massima + margine di 1 m, impostabile; con il trasferitore sollevato il materiale si ferma lì e riparte a trasferitore abbassato) ed encoder incrementale sul primo rullo a monte della prima macchina di legatura, preceduto da una fotocellula di riferimento (posizionamento preciso e avvio della carica del filo).
- **Stazione di legatura**: pacco in posizione → via a rulli ferma → compattatore chiude e conferma → mantenimento → stringifascio KNOF chiude e conferma → solo allora la KNCA 7/800 (o la SMA) avvia il ciclo, se armata → fine legatura → mantenimento e consenso all'apertura → KNOF e compattatore aprono e confermano → il pacco prosegue. Senza conferma di chiusura la legatura non parte (interblocco). Gli stati della legatrice sono: disponibile, attesa pacco, posizionamento, pacco in posizione, attesa consenso stringifascio, ciclo di legatura, ciclo completato, scarico pacco, guasto.
- **Macchine di legatura**: ogni pacco ha un solo tipo di legatura (*filo* → KNCA, *reggia d'acciaio* → SMA) e l'ottimizzatore usa solo le macchine compatibili. KNCA: la carica del filo (3 s) parte quando la testa del pacco impegna la fotocellula di riferimento; tra le legature dello stesso pattern ricarica in automatico, a pattern concluso attende il pacco successivo. SMA: ciclo di reggettatura 10 s e riarmo automatico 5 s dopo ogni ciclo (sempre armata).
- **Rigettatrice (scarto)**: dispositivo generico, disabilitato di default (la SMA non scarta pacchi): identificazione all'ingresso, abilitazione, posizionamento, espulsione con conferma, ritorno, consenso alla ripartenza.
- **Output** (menu *File*): *Esporta logica di controllo* produce le regole SE/ALLORA con condizioni AND/OR/NOT per ogni inverter (avvio, arresto, accumulo, interblocchi) e per i dispositivi, più l'elenco di fotocellule (posizione, scopo, regole) e inverter, in JSON e CSV; *Esporta specifica funzionale* genera, solo su richiesta, il documento Word dell'area (layout, macchine, sequenze in single/double banking, regole, tempi e interblocchi, risultati).
- **Fermi intermedi**: nella zona dello stopper, ognuno con intestatura di **testa** (battuta) o di **coda** (coda sul riferimento). I pacchi vengono assegnati in ordine (stopper finale, poi fermi da valle a monte) solo se le coordinate non si sovrappongono; il trasferitore preleva fino a *N pacchi per ciclo*.
- **Bilici**: il trasferitore deposita solo su bilici liberi (interblocco); posizionamento (standard o **intermedio per pacchi corti**), verifica posizione, stabilizzazione, acquisizione del peso (risoluzione), consenso a proseguire, scarico. Un pacco più lungo di un bilico viene pesato da più bilici insieme.
- Ogni dispositivo ha **consenso di avvio** e **condizione di arresto** (stessa sintassi delle regole), ritardo e **timeout di conferma**, **guasto simulato** al ciclo n e tempo di ripristino. Allarmi e interblocchi compaiono nel registro eventi, nel ciclogramma e nei risultati.

### Condizioni logiche (regole)

Nel campo *Condizione* di una regola:

```
PC(FC2) & !RUN(INV3) | SIG(Consenso)
```

Atomi: `TRUE`, `FALSE`, `PC(id)` fotocellula occupata, `RUN(modulo)` modulo in moto, `ZONE(modulo)` modulo occupato, `STR(id)` legatrice occupata, `STOPPER` pacco allo stopper, `TRANSFER_HOME`, `HEAD(daMm,aMm)` testa di un pacco nell'intervallo, `GAP(modulo,mm)` distanza dal pacco precedente minore di, `SIG(nome)` segnale, `DEV(id)` dispositivo impegnato, `DEV(id,STATO)` dispositivo in uno stato (es. `DEV(KNOF,Closed)`, `DEV(L1,StrappingCycle)`, `DEV(L1,Armed)`, `DEV(STATION,Clamping)`, `DEV(B1,Stabilizing)`). Operatori: `!` NOT, `&` AND, `|` OR, parentesi. Azioni: arresto (interblocco, priorità massima), velocità bassa, velocità alta, imposta segnale.

### Piano di legatura manuale

Nella *Stazione di legatura*, campo *Piano manuale*: `P12: L3#1 ; L2#2 + L1#3` = prima fermata L3 esegue la legatura 1, seconda fermata L2 e L1 in parallelo eseguono le legature 2 e 3. Impostare *Strategia = Manual*.

## Struttura del repository

```
src/
  LineSimulator.Models          modelli dati (POCO serializzabili), linguaggio condizioni
  LineSimulator.Core            cinematica, topologia linea, validazione, esempi
  LineSimulator.Optimization    ottimizzatore esatto delle legature e confronto strategie
  LineSimulator.Engine          motore di simulazione a tempo discreto, registrazione, risultati
  LineSimulator.Infrastructure  JSON versionato, repository/autosave, export CSV/Excel
  LineSimulator.App             interfaccia WPF (MVVM), scena 3D, ciclogramma, dashboard
tests/LineSimulator.Tests       test xUnit
examples/                       progetti JSON di esempio
docs/ARCHITETTURA.md            architettura, modello fisico, limiti noti
installer/                      script Inno Setup
.github/workflows/build.yml     CI: build, test, publish EXE, ZIP, installer, release
```

Dettagli tecnici e limiti noti: [docs/ARCHITETTURA.md](docs/ARCHITETTURA.md).
