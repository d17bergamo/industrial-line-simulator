using System.Collections.ObjectModel;
using System.Windows.Input;
using LineSimulator.App.Mvvm;
using LineSimulator.Core;
using LineSimulator.Models;

namespace LineSimulator.App.ViewModels;

/// <summary>Passo di lavoro della schermata Layout e simulazione: ogni passo mostra solo quello che serve.</summary>
public enum WorkStep { Layout, Cycle, Simulate }

/// <summary>Famiglia di macchine nella pagina Ciclo: titolo e parametri essenziali modificabili.</summary>
public sealed record CycleFamily(string Title, IReadOnlyList<PropertyItemViewModel> Items);

/// <summary>Voce della legenda dei pacchi nel passo Simula.</summary>
public sealed record LegendItem(string Name, System.Windows.Media.Brush Brush);

/// <summary>Riga degli azionamenti nella pagina Ciclo.</summary>
public sealed record DriveRow(string Module, string Role, string Drive);

/// <summary>
/// Percorso in passi (Pacchi → Layout → Ciclo → Simula e risultati) e pagina Ciclo con tutto il ciclo in un posto solo.
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
            if (_step == WorkStep.Simulate && IsPlaying) Pause();
            _step = value;
            Raise(nameof(Step)); Raise(nameof(IsStepLayout)); Raise(nameof(IsStepCycle)); Raise(nameof(IsStepSimulate));
            ShowLibrary = value == WorkStep.Layout;
            ShowInspector = value == WorkStep.Layout;
            ShowCyclogram = value == WorkStep.Simulate;
            if (value == WorkStep.Cycle) BuildCyclePage();
            if (value == WorkStep.Simulate) Raise(nameof(PackageLegend));
        }
    }
    public bool IsStepLayout => _step == WorkStep.Layout;
    public bool IsStepCycle => _step == WorkStep.Cycle;
    public bool IsStepSimulate => _step == WorkStep.Simulate;

    /// <summary>Va a un passo: "Packages" apre la schermata Pacchi, gli altri i passi della schermata Layout e simulazione.</summary>
    public ICommand GoStepCommand => _goStepCommand ??= new RelayCommand(p =>
    {
        string s = p?.ToString() ?? "";
        if (s == "Packages") { GoSectionCommand.Execute("Packages"); return; }
        if (CurrentSection != AppSection.Layout) GoSectionCommand.Execute("Layout");
        if (Enum.TryParse<WorkStep>(s, out var st)) Step = st;
    });

    public ObservableCollection<PropertyItemViewModel> CycleInfeed { get; } = new();
    public ObservableCollection<CycleFamily> CycleFamilies { get; } = new();
    public ObservableCollection<DriveRow> DriveRows { get; } = new();

    /// <summary>Riempie la pagina Ciclo: ingresso pacchi, tempi per famiglia (solo i campi essenziali) e azionamenti.</summary>
    private void BuildCyclePage()
    {
        CycleInfeed.Clear();
        foreach (var i in Essentials(_project.Infeed)) CycleInfeed.Add(i);

        CycleFamilies.Clear();
        void Fam(string title, object model) { var items = Essentials(model); if (items.Count > 0) CycleFamilies.Add(new CycleFamily(title, items)); }
        Fam($"Legatrici ({_project.Strappers.Count})", _project.StrapperDefaults);
        Fam($"Vie a rulli ({_project.Sections.Count})", _project.SectionDefaults);
        Fam($"Fotocellule ({_project.Photocells.Count})", _project.PhotocellDefaults);
        if (_project.Scales.Count > 0) Fam($"Bilici ({_project.Scales.Count})", _project.ScaleDefaults);
        Fam("Trasferitore", _project.Transfer);
        Fam("Stringifascio KNOF", _project.BundleClamp);
        Fam("Compattatore laterale", _project.Compactor);

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
}
