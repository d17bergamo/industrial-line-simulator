# Industrial Line Simulator

Applicazione desktop Windows (C# / .NET 10 / WPF) per **disegnare, configurare e simulare** una linea di movimentazione di pacchi metallici: vie a rulli a sezioni, inverter, fotocellule e logiche di accumulo, stazione con 1–3 legatrici automatiche e ottimizzazione delle legature, stopper con allineamento, trasferitore, ciclogramma dinamico, produttività e report.

> ⚠️ **Strumento di progettazione e simulazione, non un sistema di sicurezza certificato.** Protezioni e interblocchi reali vanno verificati e implementati nel PLC e nei circuiti di sicurezza della macchina.

## Stato del progetto

| Area | Stato |
|---|---|
| Motore di simulazione (rampe, fotocellule con tempo di risposta, accumulo ZPA, anti-collisione, stopper, trasferitore, legatura, deadlock) | Implementato, coperto da test |
| Ottimizzazione legature (esatta, tutte le combinazioni) + confronto automatica/manuale/predefinita | Implementato, verificato contro ricerca esaustiva |
| Regole logiche AND/OR/NOT, segnali di consenso, azioni sulle fotocellule | Implementato, coperto da test |
| Risultati, collo di bottiglia, export CSV / Excel | Implementato, coperto da test |
| Progetti JSON versionati, autosave, duplicazione, esempi | Implementato, coperto da test |
| Interfaccia WPF: vista 3D (HelixToolkit), albero, proprietà, timeline, ciclogramma sincronizzato, dashboard | Implementata; da verificare a vista su Windows |
| CI GitHub Actions: build, test, EXE self-contained, ZIP, installer Inno Setup | Configurata |

**Verifica della compilazione:** il codice è stato scritto in un ambiente senza .NET SDK (rete verso Microsoft/NuGet bloccata). La prima compilazione reale e l'esecuzione dei test avvengono nella GitHub Action su `windows-latest`. Eventuali errori segnalati dalla CI vanno corretti prima di considerare il rilascio valido.

## Requisiti

- Per **usare** il programma: Windows 10/11 x64. L'EXE è *self-contained*: non serve installare .NET.
- Per **compilare**: .NET SDK 10 (`dotnet --version` ≥ 10.0.100), Windows per il progetto WPF. I progetti del motore e i test compilano anche su Linux/macOS.

## Installazione (utente finale)

1. Scaricare da *Actions → ultimo run → Artifacts* (o dalla pagina *Releases* per i tag `v*`):
   - `IndustrialLineSimulator-win-x64.zip` → estrarre e avviare `IndustrialLineSimulator.exe`, oppure
   - `IndustrialLineSimulator-Setup-x.y.z.exe` → installer con collegamenti nel menu Start.
2. All'avvio viene caricata la linea di esempio; altri esempi in *File → Esempi*.

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

- **Albero della linea** (a sinistra): selezionare un componente per modificarne i parametri nel pannello **Proprietà**. Ogni modifica ricostruisce il modello 3D, rivalida il progetto, ricalcola le strategie di legatura e azzera la simulazione.
- **Vista 3D**: clic sinistro seleziona il componente; destro ruota; rotella zoom; viste *Alto / Laterale / Isometrica*.
- **Simulazione**: *Play* (velocità 1x/10x/50x/100x), *Pausa*, *Stop* (risultati parziali), *Reset*, *Passo* (un passo di integrazione), *Evento ▸* (fino al prossimo evento), *Fino al termine*.
- **Timeline e ciclogramma**: il cursore avanza con la simulazione; un clic sul ciclogramma o lo spostamento della timeline mostra nel 3D lo stato registrato in quell'istante. Rotella = zoom, Maiusc+rotella = scorrimento.
- **Strategie di legatura**: confronto tra combinazioni di legatrici, strategia predefinita e manuale, con motivazione della scelta.
- **Risultati**: tempo ciclo medio/min/max, pacchi/h e t/h effettivi (separati da quelli teorici), utilizzo risorse, tempi morti e blocchi, collisioni, collo di bottiglia. Export *File → Esporta CSV/Excel*.

### Condizioni logiche (regole)

Nel campo *Condizione* di una regola:

```
PC(FC2) & !RUN(INV3) | SIG(Consenso)
```

Atomi: `TRUE`, `FALSE`, `PC(id)` fotocellula occupata, `RUN(inv)` inverter in moto, `ZONE(inv)` zona occupata, `STR(id)` legatrice occupata, `STOPPER` pacco allo stopper, `TRANSFER_HOME`, `HEAD(daMm,aMm)` testa di un pacco nell'intervallo, `GAP(inv,mm)` distanza dal pacco precedente minore di, `SIG(nome)` segnale. Operatori: `!` NOT, `&` AND, `|` OR, parentesi. Azioni: arresto (interblocco, priorità massima), velocità bassa, velocità alta, imposta segnale.

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
