using System.Collections.ObjectModel;
using System.Windows.Input;
using LineSimulator.App.Mvvm;
using LineSimulator.Core;
using LineSimulator.Models;

namespace LineSimulator.App.ViewModels;

/// <summary>Passo di lavoro: 1 Pacchi (schermata a parte), 2 Dati area, 3 Layout e simulazione.</summary>
public enum WorkStep { Area, Layout }

/// <summary>Famiglia di macchine nella pagina Ciclo: titolo e parametri essenziali modificabili.</summary>
public sealed record CycleFamily(string Title, IReadOnlyList<PropertyItemViewModel> Items);

/// <summary>Voce della legenda dei pacchi nel passo Simula.</summary>
public sealed record LegendItem(string Name, System.Windows.Media.Brush Brush);

/// <summary>Riga degli azionamenti nella pagina Ciclo.</summary>
public sealed record DriveRow(string Module, string Role, string Drive);

/// <summary>
/// Percorso in passi (Pacchi → Dati area → Layout e simulazione), pagina Dati area e avvio guidato.
/// </summary>
public sealed partial class MainViewModel
{
    private WorkStep _step = WorkStep.Layout;
    private ICommand? _goStepCommand;

    public WorkStep Step
    {
        get => _step;
        set
        {
            if (_step == value) return;
            if (_step == WorkStep.Layout && IsPlaying) Pause();
            _step = value;
            Raise(nameof(Step)); Raise(nameof(IsStepLayout)); Raise(nameof(IsStepCycle));
            if (value == WorkStep.Area) BuildCyclePage();
            else { ShowLibrary = true; ShowInspector = true; Raise(nameof(PackageLegend)); }
        }
    }
    /// <summary>Passo 3: layout e simulazione nella stessa schermata.</summary>
    public bool IsStepLayout => _step == WorkStep.Layout;
    /// <summary>Passo 2: dati dell'area (obiettivo, azionamenti, tempi per famiglia).</summary>
    public bool IsStepCycle => _step == WorkStep.Area;

    /// <summary>Va a un passo: "Packages" apre la schermata Pacchi, gli altri i passi della schermata Layout e simulazione.</summary>
    public ICommand GoStepCommand => _goStepCommand ??= new RelayCommand(p =>
    {
        string s = p?.ToString() ?? "";
        if (s == "Packages") { GoSectionCommand.Execute("Packages"); return; }
        if (CurrentSection != AppSection.Layout) GoSectionCommand.Execute("Layout");
        if (Enum.TryParse<WorkStep>(s, out var st)) Step = st;
    });

    public ObservableCollection<CycleFamily> CycleFamilies { get; } = new();
    public ObservableCollection<DriveRow> DriveRows { get; } = new();

    /// <summary>Riempie la pagina Ciclo: ingresso pacchi, tempi per famiglia (solo i campi essenziali) e azionamenti.</summary>
    private void BuildCyclePage()
    {
        CycleFamilies.Clear();
        // solo le macchine che ci sono sulla linea; i dati delle legatrici dipendono dal tipo (filo o reggia)
        CycleFamily? Fam(string title, object model) { var items = Essentials(model); if (items.Count == 0) return null; var f = new CycleFamily(title, items); CycleFamilies.Add(f); return f; }
        var strappers = _project.Strappers.Where(s => s.IsEnabled).ToList();
        if (strappers.Count > 0)
        {
            bool wire = strappers.Any(s => s.MachineType == StrapperMachineType.KNCA), strap = strappers.Any(s => s.MachineType == StrapperMachineType.SMA);
            string title = wire && strap ? $"Legatrici e reggettatrici ({strappers.Count})" : strap ? $"Reggettatrici ({strappers.Count})" : $"Legatrici ({strappers.Count})";
            var f = Fam(title, _project.StrapperDefaults);
            foreach (var i in f?.Items ?? Array.Empty<PropertyItemViewModel>())
            {
                if (i.Name == nameof(StrapperGroup.ArmingTimeS)) i.LabelOverride = wire && strap ? "Ricarica filo / riarmo reggia" : strap ? "Tempo di riarmo reggia" : "Tempo di ricarica filo";
                if (i.Name == nameof(StrapperGroup.StrappingTimeS)) i.LabelOverride = strap && !wire ? "Ciclo di reggettatura" : "Ciclo di legatura";
            }
        }
        Fam($"Vie a rulli ({_project.Sections.Count})", _project.SectionDefaults);
        if (_project.Scales.Any(s => s.IsEnabled)) Fam($"Bilici ({_project.Scales.Count})", _project.ScaleDefaults);
        Fam("Trasferitore sollevabile a catene", _project.Transfer);
        Fam("Trasferitore d'accumulo", _project.AccumulationTransfer);
        if (_project.BundleClamp.IsEnabled && strappers.Count > 0) Fam("Stringifascio", _project.BundleClamp);
        if (_project.Compactor.IsEnabled) Fam("Compattatore laterale", _project.Compactor);

        DriveRows.Clear();
        var secs = _project.Sections.OrderBy(s => s.StartMm).ToList();
        var trfMod = LayoutRules.TransferModule(_project);
        for (int i = 0; i < secs.Count; i++)
        {
            var s = secs[i];
            string role = i == 0 ? "via di legatura" : ReferenceEquals(s, trfMod) ? "fronte accumulo" : "svincolo";
            string drive = StrappingAreaRules.DescribeDrive(_project, s);
            DriveRows.Add(new DriveRow(s.Id, role, drive));
        }
    }

    private List<PropertyItemViewModel> Essentials(object model)
    {
        string type = model.GetType().Name;
        return PropertyItemViewModel.For(model, OnCycleEdited, _ => null)
            .Where(i => !EssentialProperties.IsAdvanced(type, i.Name) && i.Name != "OverrideGroup" && i.Name != "Name").ToList();
    }

    private void OnCycleEdited(PropertyItemViewModel item)
    {
        GroupInheritance.PropagateAll(_project);
        RollerGroups.Propagate(_project, applyPitch: item.Name == nameof(RollerGroup.RollerPitchMm));
        if (item.Name == nameof(RollerGroup.RollerTopElevationMm)) RollerGroups.SyncElevation(_project, fromGroup: true);
        if (item.Name == nameof(RollerGroup.RollerDiameterMm) && item.Target is RollerGroup) RollerGroups.SetDiameterForAll(_project);
        ProjectChanged(structural: true);
    }

    /// <summary>Legenda dei colori dei pacchi (uno per tipologia).</summary>
    public IReadOnlyList<LegendItem> PackageLegend => _project.PackageTypes
        .Select((t, i) => new LegendItem(t.Id, new System.Windows.Media.SolidColorBrush(Rendering.LineScene.PackageColor(i)))).ToList();

    private int _inspectorTab;
    /// <summary>Scheda attiva dell'ispettore: 0 Proprietà, 1 Struttura, 2 Controllo, 3 Stato.</summary>
    public int InspectorTab { get => _inspectorTab; set => Set(ref _inspectorTab, value); }
    public bool HasCheckProblems => CheckProblems > 0;
    public string CheckProblemsLine => string.Join(" · ", CheckItems.Where(c => c.IsProblem).Take(2).Select(c => c.Message));
    private ICommand? _showCheckCommand, _goToBottleneckCommand;
    /// <summary>Apre la scheda Controllo dell'ispettore.</summary>
    public ICommand ShowCheckCommand => _showCheckCommand ??= new RelayCommand(() => { ShowInspector = true; InspectorTab = 2; });

    /// <summary>Porta il ciclo nel punto in cui il collo di bottiglia aspetta di più (o lavora più a lungo).</summary>
    public ICommand GoToBottleneckCommand => _goToBottleneckCommand ??= new RelayCommand(() =>
    {
        var id = Results?.BottleneckId;
        var rec = Recorder;
        if (string.IsNullOrEmpty(id) || rec == null) return;
        var track = rec.Tracks.FirstOrDefault(t => t.Key == id || t.Key.EndsWith(":" + id, StringComparison.Ordinal));
        if (track == null || track.Segments.Count == 0) { Status = $"Collo di bottiglia {Results!.BottleneckName}: nessuna traccia nel ciclogramma."; return; }
        var seg = track.Segments.Where(s => s.State == Engine.CycleState.Waiting).OrderByDescending(s => s.DurationS).FirstOrDefault()
                  ?? track.Segments.OrderByDescending(s => s.DurationS).First();
        if (IsPlaying) Pause();
        ShowCyclogram = true;
        DisplayTime = seg.StartS;
        Status = $"{track.Label}: {(seg.State == Engine.CycleState.Waiting ? "attesa" : "fase")} più lunga a {seg.StartS:0.0} s ({seg.DurationS:0.0} s).";
    }, () => Results != null);

    /// <summary>Riga dei risultati del passo Simula.</summary>
    public string TargetProgress => Results?.TonnesPerHour is not double tph || SimTargetTph <= 0 ? "" :
        $"{tph / SimTargetTph * 100:0}% dell'obiettivo ({SimTargetTph:0.#} t/h)";

    /// <summary>Nome e descrizione del progetto (si aggiornano con legatrici e pacchi).</summary>
    public string ProjectTitle => _project.Name;
    public string ProjectSubtitle => _project.Description;

    /// <summary>Simulazione continua (default): i pacchi entrano senza fine finché non si preme Stop.</summary>
    public bool SimContinuous
    {
        get => _project.Infeed.Continuous;
        set { if (_project.Infeed.Continuous == value) return; _project.Infeed.Continuous = value; Raise(nameof(SimContinuous)); ProjectChanged(structural: false); }
    }

    // ------------------------------------------------------------ Avvio guidato

    /// <summary>Esempio proposto all'avvio, con le scelte e le caratteristiche principali.</summary>
    public sealed record StartOption(string Name, string Summary, IReadOnlyList<string> Features, Func<LineProject> Create);

    private IReadOnlyList<StartOption>? _startOptions;
    public IReadOnlyList<StartOption> StartOptions => _startOptions ??= SampleProjects.All.Select(x =>
    {
        var p = x.Factory();
        var sec = p.Sections.OrderBy(s => s.StartMm).ToList();
        var strapSec = sec.FirstOrDefault();
        var feats = new List<string>
        {
            $"{p.Strappers.Count} {(p.Strappers.Count == 1 ? "legatrice" : "legatrici")} {string.Join(" / ", p.Strappers.Select(s => s.MachineType == StrapperMachineType.SMA ? "a reggia" : "a filo").Distinct())}, passo {(p.Strappers.Count > 1 ? $"{p.Strappers[1].PositionMm - p.Strappers[0].PositionMm:0} mm" : "—")}",
            $"Pacchi: {string.Join(", ", p.PackageTypes.Select(t => $"{t.LengthMm / 1000:0} m"))}",
            $"Via di legatura {strapSec?.LengthMm / 1000:0.#} m · svincolo {(sec.Count > 2 ? $"{sec[1].LengthMm / 1000:0.#} m" : "—")} · fronte accumulo {sec.LastOrDefault()?.LengthMm / 1000:0.#} m",
            $"Trasferitore a catene {p.Transfer.LengthMm / 1000:0.#} m · {p.Scales.Count} {(p.Scales.Count == 1 ? "bilico" : "bilici")}" + (p.Transfer.ScaleAtMidStroke ? " a metà corsa" : ""),
            p.IntermediateStops.Any(f => f.IsEnabled) ? "Fermo scomparente: double banking e intestatura di coda possibili" : "Senza fermo scomparente: intestatura di testa"
        };
        return new StartOption(x.Name, $"Modello pronto da simulare: {p.Sections.Count} moduli, lunghezza {sec.Last().EndMm / 1000:0.#} m", feats, x.Factory);
    }).ToList();

    private ICommand? _startFromExampleCommand, _startFromScratchCommand;
    /// <summary>Apre l'esempio scelto e va dritto al layout e simulazione.</summary>
    public ICommand StartFromExampleCommand => _startFromExampleCommand ??= new RelayCommand(o =>
    {
        if (o is not StartOption opt || !CanClose()) return;
        LoadProject(opt.Create(), null);
        _step = WorkStep.Area; Step = WorkStep.Layout;
        CurrentSection = AppSection.Layout;
        Status = $"Aperto l'esempio \"{opt.Name}\".";
    });
    /// <summary>Layout da zero: si parte dai pacchi (passo 1), poi i dati dell'area e il layout.</summary>
    public ICommand StartFromScratchCommand => _startFromScratchCommand ??= new RelayCommand(() =>
    {
        if (!CanClose()) return;
        LoadProject(SampleProjects.CreateEmpty(), null);
        _step = WorkStep.Area; Step = WorkStep.Layout;
        CurrentSection = AppSection.Packages;
        Status = "Nuovo layout: definisci i pacchi, poi i dati dell'area e la linea (trascina i componenti dalla libreria).";
    });
}
