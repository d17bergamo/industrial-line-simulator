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
            string drive = s.FollowsStrapping ? "inverter proprio, motori commutabili sull'inverter di legatura"
                : s.EffectiveZoneId != s.Id ? $"unito all'inverter di {s.EffectiveZoneId}"
                : "inverter proprio";
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

    /// <summary>Riga dei risultati del passo Simula.</summary>
    public string TargetProgress => Results?.TonnesPerHour is not double tph || SimTargetTph <= 0 ? "" :
        $"{tph / SimTargetTph * 100:0}% dell'obiettivo ({SimTargetTph:0.#} t/h)";
}
