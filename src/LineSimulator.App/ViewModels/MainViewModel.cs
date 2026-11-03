using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using LineSimulator.App.Mvvm;
using LineSimulator.App.Rendering;
using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Infrastructure;
using LineSimulator.Models;
using LineSimulator.Optimization;
using Microsoft.Win32;

namespace LineSimulator.App.ViewModels;

public sealed record ExampleItem(string Name, Func<LineProject> Factory);

/// <summary>Sezioni principali dell'applicazione (un'unica finestra).</summary>
public enum AppSection
{
    Home,
    Layout,
    Packages,
    Simulations
}

/// <summary>Riga dello stato delle apparecchiature durante la simulazione.</summary>
public sealed class EquipmentStatusRow : ObservableObject
{
    private string _state = string.Empty;
    private bool _alarm;
    public EquipmentStatusRow(string name) => Name = name;
    public string Name { get; }
    public string State { get => _state; set => Set(ref _state, value); }
    public bool Alarm { get => _alarm; set => Set(ref _alarm, value); }
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ProjectRepository _repo = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _autosaveTimer;
    private readonly Stopwatch _clock = new();
    private double _lastWall;
    private LineProject _project;
    private string? _filePath;
    private bool _modified;
    private SimulationEngine? _engine;
    private int _eventIndex;
    private TreeNodeViewModel? _selectedNode;
    private double _displayTime;
    private bool _isPlaying;
    private double _speedFactor = 10;
    private string _status = "Pronto";
    private int _cyclogramVersion;
    private string _strategyText = string.Empty;
    private SimulationResults? _results;
    private SimEvent? _selectedEvent;
    private bool _suppressDisplayUpdate;

    public MainViewModel()
    {
        _project = SampleProjects.CreateStandardLine();
        PropertiesView = CollectionViewSource.GetDefaultView(Properties);
        PropertiesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PropertyItemViewModel.Category)));

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) => OnTick();
        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _autosaveTimer.Tick += (_, _) => Autosave();
        _autosaveTimer.Start();

        foreach (var (name, factory) in SampleProjects.All) Examples.Add(new ExampleItem(name, factory));
        string exDir = Path.Combine(AppContext.BaseDirectory, "Examples");
        if (Directory.Exists(exDir))
            foreach (var f in Directory.GetFiles(exDir, "*.json").OrderBy(x => x))
                Examples.Add(new ExampleItem("File: " + Path.GetFileName(f), () => _repo.Load(f)));

        NewCommand = new RelayCommand(() => { if (ConfirmDiscard()) LoadProject(NewEmptyProject(), null); });
        OpenCommand = new RelayCommand(Open);
        SaveCommand = new RelayCommand(() => Save(false));
        SaveAsCommand = new RelayCommand(() => Save(true));
        DuplicateCommand = new RelayCommand(Duplicate);
        LoadExampleCommand = new RelayCommand(p => { if (p is ExampleItem ex && ConfirmDiscard()) LoadProject(ex.Factory(), null); });
        ExportCsvCommand = new RelayCommand(ExportCsv, () => _engine != null);
        AddStrapCommand = new RelayCommand(AddStrap, () => _selectedPackageType != null);
        RemoveStrapCommand = new RelayCommand(RemoveStrap, () => _selectedPackageType != null && _selectedPackageType.StrapPositionsMm.Count > 0);
        AddTransferSectionCommand = new RelayCommand(AddTransferSection);
        RemoveTransferSectionCommand = new RelayCommand(RemoveTransferSection);
        RefreshStepCommand = new RelayCommand(RefreshStep);
        ExportStepPdfCommand = new RelayCommand(ExportStepPdf, () => _engine != null);
        ExportExcelCommand = new RelayCommand(ExportExcel, () => _engine != null);

        AddSectionCommand = new RelayCommand(AddSection);
        AddPhotocellCommand = new RelayCommand(AddPhotocell);
        AddStrapperCommand = new RelayCommand(AddStrapper, () => _project.Strappers.Count < LineProject.MaxStrappers);
        AddPackageTypeCommand = new RelayCommand(AddPackageType);
        AddRuleCommand = new RelayCommand(AddRule);
        AddStopCommand = new RelayCommand(AddStop);
        AddScaleCommand = new RelayCommand(AddScale);
        DeleteCommand = new RelayCommand(DeleteSelected, () => CanDelete(SelectedNode?.Model));

        PlayCommand = new RelayCommand(Play, () => !IsPlaying);
        PauseCommand = new RelayCommand(Pause, () => IsPlaying);
        StopCommand = new RelayCommand(StopSimulation, () => _engine != null);
        ResetCommand = new RelayCommand(ResetSimulation);
        StepCommand = new RelayCommand(StepOnce, () => !IsPlaying);
        NextEventCommand = new RelayCommand(NextEvent, () => !IsPlaying);
        RunToEndCommand = new RelayCommand(RunToEnd, () => !IsPlaying);
        GoHomeCommand = new RelayCommand(() => CurrentSection = AppSection.Home);
        GoSectionCommand = new RelayCommand(p =>
        {
            if (p is AppSection sec || Enum.TryParse(p?.ToString(), out sec))
            {
                CurrentSection = sec;
                SectionOpened?.Invoke(sec);
            }
        });
        PackagePropertiesView = CollectionViewSource.GetDefaultView(PackageProperties);
        PackagePropertiesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PropertyItemViewModel.Category)));
        LibraryView = CollectionViewSource.GetDefaultView(EquipmentLibrary.Entries);
        LibraryView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LibraryEntry.Category)));
        SaveLayoutCommand = new RelayCommand(SaveLayout);
        OpenLayoutCommand = new RelayCommand(OpenLayout);
        SavePackagesCommand = new RelayCommand(SavePackages);
        OpenPackagesCommand = new RelayCommand(OpenPackages);
        DuplicateSelectedCommand = new RelayCommand(DuplicateSelected, () => CanDuplicate(SelectedNode?.Model));
        RotateSelectedCommand = new RelayCommand(RotateSelected, () => CanRotate(SelectedNode?.Model));
        NewPackageCommand = new RelayCommand(NewPackage);
        DuplicatePackageCommand = new RelayCommand(DuplicatePackage, () => SelectedPackageType != null);
        DeletePackageCommand = new RelayCommand(DeletePackage, () => SelectedPackageType != null && _project.PackageTypes.Count > 1);
        SetSpeedCommand = new RelayCommand(p => { if (double.TryParse(p?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) SpeedFactor = s; });

        LoadProject(_project, null);
    }

    // ------------------------------------------------------------ Proprietà bindabili

    public LineScene Scene { get; } = new();
    /// <summary>Scena 3D della sezione Imposta layout (stesso progetto, senza pacchi in movimento).</summary>
    public LineScene LayoutScene { get; } = new();

    // ------------------------------------------------------------ Navigazione tra le sezioni

    private AppSection _section = AppSection.Home;
    public AppSection CurrentSection
    {
        get => _section;
        set
        {
            if (!Set(ref _section, value)) return;
            if (value != AppSection.Simulations) Pause();
            Raise(nameof(SectionTitle));
        }
    }
    public string SectionTitle => _section switch
    {
        AppSection.Layout => "IMPOSTA LAYOUT",
        AppSection.Packages => "CARATTERISTICHE PACCHI",
        AppSection.Simulations => "SIMULAZIONI",
        _ => "Industrial Line Simulator"
    };
    public ICommand GoHomeCommand { get; private set; } = null!;
    public ICommand GoSectionCommand { get; private set; } = null!;
    /// <summary>La vista deve ri-inquadrare la scena della sezione appena aperta.</summary>
    public event Action<AppSection>? SectionOpened;

    // ------------------------------------------------------------ Caratteristiche pacchi

    public ObservableCollection<PackageType> PackageTypes { get; } = new();
    public ObservableCollection<PropertyItemViewModel> PackageProperties { get; } = new();
    public ICollectionView PackagePropertiesView { get; private set; } = null!;
    private PackageType? _selectedPackageType;
    public PackageType? SelectedPackageType
    {
        get => _selectedPackageType;
        set { if (Set(ref _selectedPackageType, value)) BuildPackageProperties(); }
    }
    public ICommand NewPackageCommand { get; private set; } = null!;
    public ICommand DuplicatePackageCommand { get; private set; } = null!;
    public ICommand DeletePackageCommand { get; private set; } = null!;

    // ------------------------------------------------------------ Simulazioni: pacco simulato e stato apparecchiature

    private PackageType? _simPackageType;
    private bool _syncingSimType;
    /// <summary>Tipologia di pacco simulata: una sola alla volta, la configurazione si carica automaticamente.</summary>
    public PackageType? SimPackageType
    {
        get => _simPackageType;
        set
        {
            if (_syncingSimType || value == null || !Set(ref _simPackageType, value)) return;
            _project.Infeed.TypeSequence = new List<string> { value.Id };
            ProjectChanged(structural: false);
            Status = $"Tipologia simulata: {value.Id} - {value.Name} ({value.LengthMm:0} mm, {value.WeightKg:0} kg)";
        }
    }
    public Array BankingModes { get; } = Enum.GetValues(typeof(AccumulationMode));
    public Array HeadingModes { get; } = Enum.GetValues(typeof(StopReference));

    /// <summary>Banking (single/double) scelto per la simulazione, non per il pacco.</summary>
    public AccumulationMode SimBanking
    {
        get => _project.Simulation.Banking;
        set { if (_project.Simulation.Banking == value) return; _project.Simulation.Banking = value; Raise(nameof(SimBanking)); ProjectChanged(structural: false); Status = $"Banking: {value}"; }
    }

    /// <summary>Intestatura di testa o di coda scelta per la simulazione.</summary>
    public StopReference SimHeading
    {
        get => _project.Simulation.Heading;
        set { if (_project.Simulation.Heading == value) return; _project.Simulation.Heading = value; Raise(nameof(SimHeading)); ProjectChanged(structural: false); Status = $"Intestatura: {value}"; }
    }
    public ObservableCollection<EquipmentStatusRow> EquipmentStatus { get; } = new();

    // ------------------------------------------------------------ Imposta layout: libreria, quote, modifica

    public ICollectionView LibraryView { get; private set; } = null!;
    private bool _showDistances;
    /// <summary>Mostra le quote degli equipaggiamenti rispetto all'origine (X = Y = Z = 0).</summary>
    public bool ShowDistances
    {
        get => _showDistances;
        set { if (Set(ref _showDistances, value)) LayoutScene.ShowDimensions = value; }
    }
    public ICommand SaveLayoutCommand { get; private set; } = null!;
    public ICommand OpenLayoutCommand { get; private set; } = null!;
    public ICommand SavePackagesCommand { get; private set; } = null!;
    public ICommand OpenPackagesCommand { get; private set; } = null!;
    public ICommand DuplicateSelectedCommand { get; private set; } = null!;
    public ICommand RotateSelectedCommand { get; private set; } = null!;
    public ObservableCollection<TreeNodeViewModel> Tree { get; } = new();
    public ObservableCollection<PropertyItemViewModel> Properties { get; } = new();
    public ICollectionView PropertiesView { get; }
    public ObservableCollection<ValidationIssue> Issues { get; } = new();
    public ObservableCollection<SimEvent> EventLog { get; } = new();
    public ObservableCollection<ResourceUsage> Resources { get; } = new();
    public ObservableCollection<PackageResult> PackageResults { get; } = new();
    public ObservableCollection<KeyValuePair<string, string>> Summary { get; } = new();
    public ObservableCollection<ExampleItem> Examples { get; } = new();
    public IReadOnlyList<double> Speeds { get; } = new[] { 1.0, 2.0, 5.0, 10.0, 20.0, 100.0 };

    public string Title => $"Industrial Line Simulator - {_project.Name}{(_modified ? " *" : "")}" + (_filePath != null ? $" [{Path.GetFileName(_filePath)}]" : "");
    public LineProject Project => _project;
    public SimulationRecorder? Recorder => _engine?.Recorder;
    public SimulationResults? Results { get => _results; private set { Set(ref _results, value); Raise(nameof(HasResults)); } }
    public bool HasResults => _results != null;

    public TreeNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!Set(ref _selectedNode, value)) return;
            BuildProperties();
            Scene.Highlight(value?.Model);
            LayoutScene.Highlight(value?.Model);
        }
    }

    public double DisplayTime
    {
        get => _displayTime;
        set
        {
            double v = Math.Clamp(value, 0, MaxTime);
            if (!Set(ref _displayTime, v)) return;
            Raise(nameof(DisplayTimeText));
            if (!_suppressDisplayUpdate)
            {
                if (IsPlaying) Pause();
                ShowFrameAt(v);
            }
        }
    }

    public string DisplayTimeText => DisplayTime.ToString("0.00", CultureInfo.CurrentCulture) + " s";
    public double MaxTime => _engine?.Time ?? 0;
    public bool IsPlaying { get => _isPlaying; private set => Set(ref _isPlaying, value); }
    public double SpeedFactor { get => _speedFactor; set => Set(ref _speedFactor, value); }

    // ------------------------------------------------------------ Pop-up velocità di linea e produttività oraria

    private double? _lineSpeedOverride;
    private double? _liveMeasuredPph;
    private int _lastExitedForPph = -1;

    public double MinLineSpeed => SimulationEngine.MinLineSpeedMps;
    public double MaxLineSpeed => SimulationEngine.MaxLineSpeedMps;

    /// <summary>Velocità di trasporto della linea (0,5 - 1,5 m/s); cambiata a simulazione in corso vale subito.</summary>
    public double LineSpeedMps
    {
        get => _lineSpeedOverride ?? Math.Clamp(_project.Sections.Count > 0 ? _project.Sections.Min(x => x.HighSpeed) : 1.0, MinLineSpeed, MaxLineSpeed);
        set
        {
            double v = Math.Round(Math.Clamp(double.IsFinite(value) ? value : MinLineSpeed, MinLineSpeed, MaxLineSpeed), 2);
            if (_lineSpeedOverride == v) return;
            _lineSpeedOverride = v;
            _engine?.SetLineSpeed(v);
            _liveMeasuredPph = null; _lastExitedForPph = -1;
            Raise(nameof(LineSpeedMps)); RaiseProductivity();
            Status = $"Velocità di linea: {v:0.00} m/s";
        }
    }

    private PackageType? ReferencePackage => _simPackageType ?? _project.PackageTypes.FirstOrDefault();

    /// <summary>Produttività teorica alla velocità impostata: 3600 x v / L pacco.</summary>
    public double TheoreticalPph
        => ReferencePackage is { LengthMm: > 0 } t ? TheoreticalProductivity.PackagesPerHour(LineSpeedMps, t.LengthMm / 1000.0) : 0;

    public string ProductivityText
    {
        get
        {
            var t = ReferencePackage;
            string theo = $"Teorica a {LineSpeedMps:0.00} m/s: {TheoreticalPph:0.0} pacchi/h"
                + (t != null ? $" ({TheoreticalPph * t.WeightKg / 1000.0:0.0} t/h)" : "");
            return _liveMeasuredPph is double m ? theo + $"\nMisurata in simulazione: {m:0.0} pacchi/h" : theo + "\nMisurata: disponibile dopo i primi pacchi usciti";
        }
    }

    // ------------------------------------------------------------ Ciclogramma a gradini (tempi arrotondati a 0,5 s solo qui)

    private IReadOnlyList<StepRow>? _stepRows;
    public IReadOnlyList<StepRow>? StepRows { get => _stepRows; private set => Set(ref _stepRows, value); }
    public ICommand RefreshStepCommand { get; private set; } = null!;
    public ICommand ExportStepPdfCommand { get; private set; } = null!;

    private void RefreshStep()
    {
        StepRows = _engine == null ? null : StepCyclogram.Build(_engine.Recorder);
        Status = StepRows == null ? "Nessuna simulazione: ciclogramma a gradini non disponibile." : $"Ciclogramma a gradini aggiornato ({StepRows.Count} utenze, {StepCyclogram.TotalS(StepRows):0.0} s).";
    }

    private void ExportStepPdf()
    {
        RefreshStep();
        if (StepRows == null || StepRows.Count == 0) { Status = "Esegui prima una simulazione."; return; }
        var dlg = new SaveFileDialog { Filter = "PDF (*.pdf)|*.pdf", FileName = "ciclogramma-a-gradini.pdf" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            StepCyclogramPdf.Save(dlg.FileName, StepRows, _project.Name.Length > 0 ? _project.Name : "Linea di legatura");
            Status = "Ciclogramma a gradini esportato: " + dlg.FileName;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Esportazione PDF", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void RaiseProductivity() { Raise(nameof(TheoreticalPph)); Raise(nameof(ProductivityText)); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public int CyclogramVersion { get => _cyclogramVersion; private set => Set(ref _cyclogramVersion, value); }
    public string StrategyText { get => _strategyText; private set => Set(ref _strategyText, value); }

    public SimEvent? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (Set(ref _selectedEvent, value) && value != null) DisplayTime = value.TimeS;
        }
    }

    public ICommand NewCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand LoadExampleCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportExcelCommand { get; }
    public ICommand AddSectionCommand { get; }
    public ICommand AddPhotocellCommand { get; }
    public ICommand AddStrapperCommand { get; }
    public ICommand AddPackageTypeCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand AddStopCommand { get; }
    public ICommand AddScaleCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand StepCommand { get; }
    public ICommand NextEventCommand { get; }
    public ICommand RunToEndCommand { get; }
    public ICommand SetSpeedCommand { get; }

    /// <summary>La vista deve ri-inquadrare la scena (nuovo progetto).</summary>
    public event Action? SceneRebuilt;

    // ------------------------------------------------------------ Progetto

    private static LineProject NewEmptyProject()
    {
        var p = new LineProject { Name = "Nuova linea" };
        p.Sections.Add(new RollerSection { Id = "M1", Name = "Modulo 1", StartMm = 0, LengthMm = 14000, RollerCount = 47 });
        ProjectNormalizer.EnsureEntryPhotocell(p);
        p.Stopper = new StopperConfig { Id = "STP", Name = "Stopper", PositionMm = 13500 };
        p.Transfer = new TransferConfig { Id = "TRF", Name = "Trasferitore", PositionMm = 1500, LengthMm = 12000 };
        p.StrappingStation.IsEnabled = false;
        p.PackageTypes.Add(new PackageType { Id = "P12", Name = "Pacco 12 m" });
        p.Infeed.TypeSequence.Add("P12");
        return p;
    }

    private void LoadProject(LineProject p, string? path)
    {
        Pause();
        _project = p;
        ProjectNormalizer.AlignStopperToLastRoller(_project);   // il fermo finale sta sempre dopo l'ultimo rullo
        _filePath = path;
        _modified = false;
        _engine = null;
        Results = null;
        Scene.Build(_project);
        LayoutScene.Build(_project);
        NormalizeSimulatedType();
        RefreshPackages();
        BuildTree();
        SelectedNode = Tree.FirstOrDefault();
        ProjectChanged(structural: false, markModified: false);
        SceneRebuilt?.Invoke();
    }

    private bool ConfirmDiscard()
    {
        if (!_modified) return true;
        var r = MessageBox.Show("Il progetto ha modifiche non salvate. Salvarle?", "Industrial Line Simulator",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return Save(false);
        return true;
    }

    public bool CanClose() => ConfirmDiscard();

    private void Open()
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Filter = "Progetto linea (*.json)|*.json|Tutti i file|*.*" };
        if (dlg.ShowDialog() != true) return;
        try { LoadProject(_repo.Load(dlg.FileName), dlg.FileName); }
        catch (Exception ex) { MessageBox.Show("Impossibile aprire il progetto:\n" + ex.Message, "Errore", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private bool Save(bool saveAs)
    {
        string? path = _filePath;
        if (saveAs || path == null)
        {
            var dlg = new SaveFileDialog { Filter = "Progetto linea (*.json)|*.json", FileName = SafeName(_project.Name) + ".json" };
            if (dlg.ShowDialog() != true) return false;
            path = dlg.FileName;
        }
        try
        {
            _repo.Save(_project, path);
            _filePath = path;
            _modified = false;
            Raise(nameof(Title));
            Status = "Progetto salvato: " + path;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Salvataggio non riuscito:\n" + ex.Message, "Errore", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void Duplicate()
    {
        var copy = ProjectSerializer.Duplicate(_project);
        LoadProject(copy, null);
        _modified = true;
        Raise(nameof(Title));
        Status = "Progetto duplicato: salvarlo con un nuovo nome.";
    }

    private void Autosave()
    {
        if (!_modified) return;
        try { Status = "Salvataggio automatico: " + _repo.Autosave(_project); }
        catch (Exception ex) { Status = "Salvataggio automatico non riuscito: " + ex.Message; }
    }

    private static string SafeName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>Qualsiasi modifica ai parametri: ricostruzione 3D, validazione, strategie e azzeramento simulazione.</summary>
    private void ProjectChanged(bool structural, bool markModified = true)
    {
        if (markModified) _modified = true;
        ProjectNormalizer.AlignStopperToLastRoller(_project);   // segue l'ultimo rullo quando si modificano o spostano le vie a rulli
        var selected = SelectedNode?.Model;
        Scene.Build(_project);
        Scene.Highlight(selected);
        LayoutScene.Build(_project);
        LayoutScene.Highlight(selected);
        if (structural) RefreshPackages();
        if (structural) BuildTree(selected);
        else UpdateTreeHeaders();
        ResetSimulation();
        RefreshValidation();
        RefreshStrategies();
        UpdateEquipmentStatus(_engine == null ? null : _engine.CaptureFrame());
        Raise(nameof(Title));
    }

    private void RefreshValidation()
    {
        Issues.Clear();
        foreach (var i in ProjectValidator.Validate(_project).Issues.OrderByDescending(i => i.Severity)) Issues.Add(i);
    }

    private void RefreshStrategies()
    {
        var sb = new StringBuilder();
        try
        {
            var topo = new LineTopology(_project);
            if (!_project.StrappingStation.IsEnabled || topo.StrappingZone == null)
                sb.AppendLine("Stazione di legatura disabilitata o zona non definita.");
            foreach (var t in _project.PackageTypes.Where(t => t.StrapPositionsMm.Count > 0))
            {
                if (topo.StrappingZone == null) break;
                var cmp = StrappingPlanner.Compare(_project, topo, t);
                sb.AppendLine($"══ Tipologia {t.Id} - {t.Name} ({t.LengthMm:0} mm, legature a {string.Join(", ", t.StrapPositionsMm.Select(x => x.ToString("0", CultureInfo.CurrentCulture)))} mm)");
                sb.AppendLine();
                sb.AppendLine("Piano selezionato:");
                sb.AppendLine(cmp.Selected.Describe());
                sb.AppendLine();
                sb.AppendLine("Strategia predefinita:");
                sb.AppendLine(cmp.Default.Describe());
                if (cmp.Manual != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("Strategia manuale:");
                    sb.AppendLine(cmp.Manual.Describe());
                }
                sb.AppendLine();
                sb.AppendLine("Motivazione della scelta:");
                sb.AppendLine(cmp.SelectionReason);
                sb.AppendLine();
            }
        }
        catch (Exception ex) { sb.AppendLine("Analisi non disponibile: " + ex.Message); }
        StrategyText = sb.ToString();
    }

    // ------------------------------------------------------------ Albero e proprietà

    private void BuildTree(object? reselect = null)
    {
        Tree.Clear();
        var p = _project;
        var root = new TreeNodeViewModel("Progetto: " + p.Name, p, "Project");
        root.Children.Add(new TreeNodeViewModel("Layout 3D", p.Layout, "Layout"));
        root.Children.Add(Group("Moduli via a rulli (inverter)", "Section", p.Sections.OrderBy(s => s.StartMm)));
        root.Children.Add(Group("Fotocellule", "Photocell", p.Photocells.OrderBy(s => s.PositionMm)));
        root.Children.Add(new TreeNodeViewModel(DeviceHeader("Rigettatrice SMA", p.Rejector), p.Rejector, "Rejector"));
        var station = new TreeNodeViewModel("Stazione di legatura", p.StrappingStation, "Station");
        station.Children.Add(new TreeNodeViewModel(DeviceHeader("Stringifascio KNOF", p.BundleClamp), p.BundleClamp, "BundleClamp"));
        station.Children.Add(new TreeNodeViewModel(DeviceHeader("Compattatore laterale", p.Compactor), p.Compactor, "Compactor"));
        foreach (var s in p.Strappers) station.Children.Add(new TreeNodeViewModel(s.ToString(), s, "Strapper"));
        root.Children.Add(station);
        root.Children.Add(Group("Fermi intermedi", "Stop", p.IntermediateStops.OrderBy(s => s.PositionMm)));
        root.Children.Add(new TreeNodeViewModel("Stopper", p.Stopper, "Stopper"));
        root.Children.Add(new TreeNodeViewModel("Trasferitore", p.Transfer, "Transfer"));
        root.Children.Add(Group("Bilici di pesatura", "Scale", p.Scales.OrderBy(s => s.StartMm)));
        root.Children.Add(Group("Tipologie pacco", "PackageType", p.PackageTypes));
        root.Children.Add(new TreeNodeViewModel("Ingresso pacchi", p.Infeed, "Infeed"));
        root.Children.Add(Group("Regole logiche", "Rule", p.Rules));
        root.Children.Add(new TreeNodeViewModel("Impostazioni simulazione", p.Simulation, "Simulation"));
        Tree.Add(root);

        if (reselect != null)
        {
            var node = root.SelfAndDescendants().FirstOrDefault(n => ReferenceEquals(n.Model, reselect));
            if (node != null)
            {
                _selectedNode = node;
                node.IsSelected = true;
                Raise(nameof(SelectedNode));
            }
        }
    }

    private static string DeviceHeader(string label, SequenceDeviceConfig d) => d.IsEnabled ? $"{label} ({d.Id})" : $"{label} (disabilitato)";

    private static TreeNodeViewModel Group<T>(string header, string kind, IEnumerable<T> items) where T : ComponentBase
    {
        var g = new TreeNodeViewModel(header, null, kind + "Group");
        foreach (var i in items) g.Children.Add(new TreeNodeViewModel(i.ToString(), i, kind));
        return g;
    }

    private void UpdateTreeHeaders()
    {
        foreach (var n in Tree.SelectMany(t => t.SelfAndDescendants()))
        {
            if (n.Model is BundleClampConfig k) n.Header = DeviceHeader("Stringifascio KNOF", k);
            else if (n.Model is SideCompactorConfig cm) n.Header = DeviceHeader("Compattatore laterale", cm);
            else if (n.Model is RejectorConfig rj) n.Header = DeviceHeader("Rigettatrice SMA", rj);
            else if (n.Model is ComponentBase c) n.Header = c.ToString();
            else if (n.Model is LineProject p) n.Header = "Progetto: " + p.Name;
        }
    }

    /// <summary>Seleziona il nodo corrispondente a un oggetto (es. clic nella vista 3D).</summary>
    public void SelectModel(object? model)
    {
        if (model == null) return;
        var node = Tree.SelectMany(t => t.SelfAndDescendants()).FirstOrDefault(n => ReferenceEquals(n.Model, model));
        if (node == null) return;
        foreach (var n in Tree.SelectMany(t => t.SelfAndDescendants())) n.IsSelected = false;
        node.IsSelected = true;
        SelectedNode = node;
    }

    // ---- sezioni del trasferitore sollevabile (numero, nome, lunghezza)

    public ObservableCollection<TransferSectionRow> TransferSections { get; } = new();
    public bool TransferSectionsVisible => SelectedNode?.Model is TransferConfig;
    public ICommand AddTransferSectionCommand { get; private set; } = null!;
    public ICommand RemoveTransferSectionCommand { get; private set; } = null!;
    private bool _loadingSections;

    private void LoadTransferSections()
    {
        _loadingSections = true;
        TransferSections.Clear();
        if (SelectedNode?.Model is TransferConfig tc)
            foreach (var sec in tc.EffectiveSections())
            {
                var row = new TransferSectionRow { Name = sec.Name, LengthMm = sec.LengthMm };
                row.PropertyChanged += (_, _) => CommitTransferSections();
                TransferSections.Add(row);
            }
        _loadingSections = false;
        Raise(nameof(TransferSectionsVisible));
    }

    private void CommitTransferSections()
    {
        if (_loadingSections || SelectedNode?.Model is not TransferConfig tc) return;
        tc.SetSections(TransferSections.Where(r => r.LengthMm > 0).Select(r => new TransferSection { Name = r.Name, LengthMm = r.LengthMm }));
        ProjectChanged(structural: true);
        foreach (var it in Properties) it.Refresh();
    }

    private void AddTransferSection()
    {
        if (SelectedNode?.Model is not TransferConfig) return;
        var row = new TransferSectionRow { Name = "Sezione " + (TransferSections.Count + 1), LengthMm = 3000 };
        row.PropertyChanged += (_, _) => CommitTransferSections();
        TransferSections.Add(row);
        CommitTransferSections();
    }

    private void RemoveTransferSection(object? row)
    {
        if (row is TransferSectionRow r && TransferSections.Count > 1) { TransferSections.Remove(r); CommitTransferSections(); }
    }

    private void BuildProperties()
    {
        LoadTransferSections();
        Properties.Clear();
        var model = SelectedNode?.Model;
        if (model == null) return;
        foreach (var item in PropertyItemViewModel.For(model, OnPropertyEdited, IdOptions))
            Properties.Add(item);
    }

    private IReadOnlyList<string>? IdOptions(string propertyName) => propertyName switch
    {
        "InverterId" or "ControlledInverterId" or "TargetInverterId" => _project.Sections.OrderBy(i => i.StartMm).Select(i => i.Id).ToList(),
        "PackageTypeId" => _project.PackageTypes.Select(i => i.Id).ToList(),
        _ => null
    };

    private void OnPropertyEdited(PropertyItemViewModel item)
    {
        if (SelectedNode?.Model is RollerSection rs && (item.Name is "RollerCount" or "RollerPitchMm" or "EndOverhangMm"))
            rs.RecalculateLength();
        bool structural = item.Name is "Id" or "StartMm" or "PositionMm" or "DistanceFromLastRollerMm" or "InverterId" or "IsEnabled" or "RollerCount" or "RollerPitchMm" or "EndOverhangMm";
        ProjectChanged(structural);
        foreach (var other in Properties) if (other != item) other.Refresh();
    }

    // ------------------------------------------------------------ Aggiunta / eliminazione componenti

    private string NextId(string prefix, IEnumerable<ComponentBase> existing)
    {
        var ids = existing.Select(e => e.Id).ToHashSet();
        int n = 1;
        while (ids.Contains(prefix + n)) n++;
        return prefix + n;
    }

    private IEnumerable<ComponentBase> AllComponents() =>
        _project.Sections.Cast<ComponentBase>().Concat(_project.Photocells)
            .Concat(_project.Strappers).Concat(_project.PackageTypes).Concat(_project.Rules)
            .Concat(_project.IntermediateStops).Concat(_project.Scales);

    /// <summary>Nuovo modulo di via a rulli in coda alla linea (con il proprio inverter, parametri copiati dall'ultimo modulo).</summary>
    private void AddSection()
    {
        var last = _project.Sections.OrderBy(x => x.StartMm).LastOrDefault();
        double start = last?.EndMm ?? 0;
        var s = new RollerSection { Id = NextId("M", AllComponents()), StartMm = start, LengthMm = 6000, RollerCount = 20 };
        s.Name = "Modulo " + s.Id;
        if (last != null)
        {
            s.WidthMm = last.WidthMm; s.RollerPitchMm = last.RollerPitchMm; s.RollerDiameterMm = last.RollerDiameterMm;
            s.LowSpeed = last.LowSpeed; s.HighSpeed = last.HighSpeed; s.Acceleration = last.Acceleration; s.Deceleration = last.Deceleration;
            s.DefaultSpeed = last.DefaultSpeed; s.AutoStopWhenEmpty = last.AutoStopWhenEmpty;
        }
        _project.Sections.Add(s);
        AfterAdd(s);
    }

    private void AddPhotocell()
    {
        var topo = new LineTopology(_project);
        double mid = (topo.LineStartM + topo.LineEndM) / 2 * 1000;
        var pc = new PhotocellConfig { Id = NextId("FC", AllComponents()), Name = "Nuova fotocellula", PositionMm = Math.Round(mid) };
        _project.Photocells.Add(pc);
        AfterAdd(pc);
    }

    private void AddStrapper()
    {
        if (_project.Strappers.Count >= LineProject.MaxStrappers) return;
        double pos = _project.Strappers.Count > 0 ? _project.Strappers.Max(s => s.PositionMm) + 4500 : 30000;
        var s = new StrapperConfig { Id = NextId("L", AllComponents()), Name = $"Legatrice {_project.Strappers.Count + 1}", PositionMm = pos };
        _project.Strappers.Add(s);
        AfterAdd(s);
    }

    private void AddPackageType()
    {
        var t = new PackageType { Id = NextId("T", AllComponents()), Name = "Nuova tipologia" };
        _project.PackageTypes.Add(t);
        AfterAdd(t);
    }

    private void AddRule()
    {
        var r = new LogicRule
        {
            Id = NextId("R", AllComponents()), Name = "Nuova regola", TargetInverterId = _project.Sections.OrderBy(x => x.StartMm).FirstOrDefault()?.Id ?? "",
            Action = RuleAction.SetLowSpeed, Condition = new ConstantCondition { Value = false }
        };
        _project.Rules.Add(r);
        AfterAdd(r);
    }

    private void AddStop()
    {
        double pos = _project.Stopper.PositionMm - (_project.PackageTypes.Count > 0 ? _project.PackageTypes.Min(t => t.LengthMm) : 6000)
                     - _project.Simulation.MinSafetyDistanceMm - 100;
        var s = new IntermediateStopConfig { Id = NextId("FM", AllComponents()), Name = "Fermo intermedio", PositionMm = Math.Round(pos) };
        _project.IntermediateStops.Add(s);
        if (_project.Transfer.BatchSize < 1 + _project.IntermediateStops.Count(x => x.IsEnabled))
            _project.Transfer.BatchSize = 1 + _project.IntermediateStops.Count(x => x.IsEnabled);
        AfterAdd(s);
    }

    private void AddScale()
    {
        double start = _project.Scales.Count > 0 ? _project.Scales.Max(s => s.EndMm) + 100 : _project.Transfer.PositionMm;
        var s = new WeighingScaleConfig { Id = NextId("B", AllComponents()), Name = "Bilico", StartMm = start, LengthMm = 6000 };
        _project.Scales.Add(s);
        AfterAdd(s);
    }

    private void AfterAdd(object model)
    {
        ProjectChanged(structural: true);
        SelectModel(model);
    }

    private static bool CanDelete(object? m) => m is RollerSection or PhotocellConfig or StrapperConfig or PackageType or LogicRule
        or IntermediateStopConfig or WeighingScaleConfig or BundleClampConfig or SideCompactorConfig or RejectorConfig;

    private void DeleteSelected()
    {
        var m = SelectedNode?.Model;
        if (!CanDelete(m)) return;
        if (MessageBox.Show($"Eliminare {m}?", "Conferma", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        switch (m)
        {
            case RollerSection s: _project.Sections.Remove(s); break;
            case PhotocellConfig pc: _project.Photocells.Remove(pc); break;
            case StrapperConfig st: _project.Strappers.Remove(st); break;
            case PackageType t: _project.PackageTypes.Remove(t); break;
            case LogicRule r: _project.Rules.Remove(r); break;
            case IntermediateStopConfig fs: _project.IntermediateStops.Remove(fs); break;
            case WeighingScaleConfig sc: _project.Scales.Remove(sc); break;
            // dispositivi singoli del progetto: "eliminare" = rimuoverli dal layout (disabilitarli)
            case BundleClampConfig k: k.IsEnabled = false; break;
            case SideCompactorConfig cm: cm.IsEnabled = false; break;
            case RejectorConfig rj: rj.IsEnabled = false; break;
        }
        _selectedNode = null;
        ProjectChanged(structural: true);
        SelectedNode = Tree.FirstOrDefault();
    }

    // ------------------------------------------------------------ Layout: inserimento da libreria, spostamento, rotazione, duplicazione

    private static double Snap(double mm) => Math.Round(mm / 10.0) * 10.0;

    /// <summary>Inserisce nel layout la macchina scelta nella libreria, nel punto indicato (X in mm lungo la linea, Y in m).</summary>
    public void PlaceFromLibrary(LibraryEntry e, double xMm, double yM)
    {
        object? added = null;
        double x = Snap(xMm);
        switch (e.Key)
        {
            case "roller":
            {
                var last = _project.Sections.OrderBy(v => v.StartMm).LastOrDefault();
                var sec = new RollerSection { Id = NextId("M", AllComponents()), RollerCount = 20, RollerPitchMm = 300, LengthMm = 6000 };
                sec.Name = "Modulo " + sec.Id;
                if (last != null)
                {
                    sec.WidthMm = last.WidthMm; sec.RollerPitchMm = last.RollerPitchMm; sec.RollerDiameterMm = last.RollerDiameterMm;
                    sec.LowSpeed = last.LowSpeed; sec.HighSpeed = last.HighSpeed; sec.Acceleration = last.Acceleration; sec.Deceleration = last.Deceleration;
                    sec.DefaultSpeed = last.DefaultSpeed; sec.AutoStopWhenEmpty = last.AutoStopWhenEmpty;
                }
                sec.EndOverhangMm = 150;
                sec.RecalculateLength();
                sec.StartMm = Math.Max(0, Snap(x - sec.LengthMm / 2));
                _project.Sections.Add(sec);
                added = sec;
                break;
            }
            case "knca":
            case "sma":
            {
                if (_project.Strappers.Count >= LineProject.MaxStrappers) { Status = $"Massimo {LineProject.MaxStrappers} legatrici."; return; }
                var st = new StrapperConfig { Id = NextId("L", AllComponents()), Name = $"Legatrice {_project.Strappers.Count + 1}", PositionMm = x, Side = yM >= 0 ? LateralSide.Left : LateralSide.Right };
                if (e.Key == "sma") st.MachineType = StrapperMachineType.SMA;   // reggia d'acciaio: default 10 s + riarmo 5 s
                _project.Strappers.Add(st);
                added = st;
                break;
            }
            case "knof":
                _project.BundleClamp.IsEnabled = true;
                _project.BundleClamp.PositionMm = x;
                added = _project.BundleClamp;
                break;
            case "compactor":
                _project.Compactor.IsEnabled = true;
                _project.Compactor.PositionMm = x;
                added = _project.Compactor;
                break;
            case "rejector":
                _project.Rejector.IsEnabled = true;
                _project.Rejector.PositionMm = Math.Max(0, Snap(x - _project.Rejector.LengthMm / 2));
                _project.Rejector.Side = yM >= 0 ? LateralSide.Right : LateralSide.Left;
                added = _project.Rejector;
                break;
            case "scale":
            {
                var sc = new WeighingScaleConfig { Id = NextId("B", AllComponents()), Name = "Bilico", LengthMm = 6000 };
                sc.StartMm = Math.Max(0, Snap(x - sc.LengthMm / 2));
                _project.Scales.Add(sc);
                added = sc;
                break;
            }
            case "stop":
            {
                var fs = new IntermediateStopConfig { Id = NextId("FM", AllComponents()), Name = "Fermo intermedio", PositionMm = x };
                _project.IntermediateStops.Add(fs);
                added = fs;
                break;
            }
            case "photocell":
            {
                var pc = new PhotocellConfig { Id = NextId("FC", AllComponents()), Name = "Nuova fotocellula", PositionMm = x };
                _project.Photocells.Add(pc);
                added = pc;
                break;
            }
        }
        if (added == null) return;
        AfterAdd(added);
        Status = $"Inserito: {e.Name} a X = {x / 1000.0:0.000} m";
    }

    public static bool CanMove(object? m) => m is RollerSection or PhotocellConfig or StrapperConfig or BundleClampConfig or SideCompactorConfig
        or RejectorConfig or IntermediateStopConfig or TransferConfig or WeighingScaleConfig;

    /// <summary>Sposta il componente selezionato lungo la linea (dx in mm); per i dispositivi laterali y (m) ne decide il lato.</summary>
    public void MoveSelectedBy(double dxMm, double yM)
    {
        var m = SelectedNode?.Model;
        if (!CanMove(m)) return;
        switch (m)
        {
            case RollerSection s: s.StartMm += dxMm; break;
            case PhotocellConfig pc: pc.PositionMm += dxMm; break;
            case StrapperConfig st: st.PositionMm += dxMm; st.Side = yM >= 0 ? LateralSide.Left : LateralSide.Right; break;
            case BundleClampConfig k: k.PositionMm += dxMm; break;
            case SideCompactorConfig c: c.PositionMm += dxMm; break;
            case RejectorConfig r: r.PositionMm += dxMm; break;
            case IntermediateStopConfig fs: fs.PositionMm += dxMm; break;
            case TransferConfig t: t.PositionMm += dxMm; break;
            case WeighingScaleConfig sc: sc.StartMm += dxMm; break;
        }
        ProjectNormalizer.AlignStopperToLastRoller(_project);
        LayoutScene.Build(_project);
        LayoutScene.Highlight(m);
        Scene.Build(_project);                  // anche lo spazio di simulazione segue lo spostamento
        Scene.Highlight(m);
        foreach (var item in Properties) item.Refresh();
    }

    /// <summary>Fine del trascinamento: posizione arrotondata a 10 mm e aggiornamento completo del progetto.</summary>
    public void CommitMove()
    {
        var m = SelectedNode?.Model;
        switch (m)
        {
            case RollerSection s: s.StartMm = Math.Max(0, Snap(s.StartMm)); break;
            case PhotocellConfig pc: pc.PositionMm = Snap(pc.PositionMm); break;
            case StrapperConfig st: st.PositionMm = Snap(st.PositionMm); break;
            case BundleClampConfig k: k.PositionMm = Snap(k.PositionMm); break;
            case SideCompactorConfig c: c.PositionMm = Snap(c.PositionMm); break;
            case RejectorConfig r: r.PositionMm = Snap(r.PositionMm); break;
            case IntermediateStopConfig fs: fs.PositionMm = Snap(fs.PositionMm); break;
            case TransferConfig t: t.PositionMm = Snap(t.PositionMm); break;
            case WeighingScaleConfig sc: sc.StartMm = Snap(sc.StartMm); break;
        }
        ProjectChanged(structural: true);
        BuildProperties();
    }

    /// <summary>Rotazione di 180° attorno alla verticale: porta il dispositivo sul lato opposto della linea.</summary>
    private static bool CanRotate(object? m) => m is StrapperConfig or RejectorConfig or TransferConfig;

    private void RotateSelected()
    {
        switch (SelectedNode?.Model)
        {
            case StrapperConfig st: st.Side = st.Side == LateralSide.Left ? LateralSide.Right : LateralSide.Left; break;
            case RejectorConfig r: r.Side = r.Side == LateralSide.Left ? LateralSide.Right : LateralSide.Left; break;
            case TransferConfig t: t.DestinationSide = t.DestinationSide == LateralSide.Left ? LateralSide.Right : LateralSide.Left; break;
            default: return;
        }
        ProjectChanged(structural: false);
        BuildProperties();
    }

    private static bool CanDuplicate(object? m) => m is RollerSection or PhotocellConfig or StrapperConfig or IntermediateStopConfig or WeighingScaleConfig;

    private static T CloneOf<T>(T src) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(src, ProjectSerializer.Options), ProjectSerializer.Options)!;

    private void DuplicateSelected()
    {
        switch (SelectedNode?.Model)
        {
            case RollerSection s:
            {
                var c = CloneOf(s);
                c.Id = NextId("M", AllComponents()); c.Name = "Modulo " + c.Id; c.StartMm = s.EndMm;
                _project.Sections.Add(c); AfterAdd(c); break;
            }
            case PhotocellConfig pc:
            {
                var c = CloneOf(pc);
                c.Id = NextId("FC", AllComponents()); c.Name = pc.Name + " (copia)"; c.PositionMm = pc.PositionMm + 500;
                _project.Photocells.Add(c); AfterAdd(c); break;
            }
            case StrapperConfig st:
            {
                if (_project.Strappers.Count >= LineProject.MaxStrappers) { Status = $"Massimo {LineProject.MaxStrappers} legatrici."; return; }
                var c = CloneOf(st);
                c.Id = NextId("L", AllComponents()); c.Name = st.Name + " (copia)"; c.PositionMm = st.PositionMm + 4500;
                _project.Strappers.Add(c); AfterAdd(c); break;
            }
            case IntermediateStopConfig fs:
            {
                var c = CloneOf(fs);
                c.Id = NextId("FM", AllComponents()); c.Name = fs.Name + " (copia)"; c.PositionMm = fs.PositionMm + 1000;
                _project.IntermediateStops.Add(c); AfterAdd(c); break;
            }
            case WeighingScaleConfig sc:
            {
                var c = CloneOf(sc);
                c.Id = NextId("B", AllComponents()); c.Name = sc.Name + " (copia)"; c.StartMm = sc.EndMm + 100;
                _project.Scales.Add(c); AfterAdd(c); break;
            }
        }
    }

    // ------------------------------------------------------------ Layout e tipologie: file indipendenti

    private void SaveLayout()
    {
        var dlg = new SaveFileDialog { Filter = "Layout linea (*.layout.json)|*.layout.json|JSON (*.json)|*.json", FileName = SafeName(_project.Name) + ".layout.json" };
        if (dlg.ShowDialog() != true) return;
        try { SplitFiles.SaveLayout(_project, dlg.FileName); Status = "Layout salvato: " + dlg.FileName; }
        catch (Exception ex) { MessageBox.Show("Salvataggio non riuscito:\n" + ex.Message, "Errore", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OpenLayout()
    {
        var dlg = new OpenFileDialog { Filter = "Layout linea (*.json)|*.json|Tutti i file|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var layout = SplitFiles.LoadLayout(dlg.FileName, _project);
            LoadProject(layout, null);
            _modified = true;
            Raise(nameof(Title));
            Status = "Layout caricato (tipologie di pacco mantenute): " + dlg.FileName;
        }
        catch (Exception ex) { MessageBox.Show("Impossibile aprire il layout:\n" + ex.Message, "Errore", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SavePackages()
    {
        var dlg = new SaveFileDialog { Filter = "Tipologie pacchi (*.pacchi.json)|*.pacchi.json|JSON (*.json)|*.json", FileName = "tipologie.pacchi.json" };
        if (dlg.ShowDialog() != true) return;
        try { SplitFiles.SavePackages(_project.PackageTypes, dlg.FileName); Status = "Tipologie salvate: " + dlg.FileName; }
        catch (Exception ex) { MessageBox.Show("Salvataggio non riuscito:\n" + ex.Message, "Errore", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OpenPackages()
    {
        var dlg = new OpenFileDialog { Filter = "Tipologie pacchi (*.json)|*.json|Tutti i file|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var types = SplitFiles.LoadPackages(dlg.FileName);
            _project.PackageTypes = types;
            _project.Infeed.TypeSequence = new List<string> { types[0].Id };
            ProjectChanged(structural: true);
            Status = $"Caricate {types.Count} tipologie di pacco: " + dlg.FileName;
        }
        catch (Exception ex) { MessageBox.Show("Impossibile aprire le tipologie:\n" + ex.Message, "Errore", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // ------------------------------------------------------------ Pacchi

    private void RefreshPackages()
    {
        var keep = _selectedPackageType != null && _project.PackageTypes.Contains(_selectedPackageType) ? _selectedPackageType : _project.PackageTypes.FirstOrDefault();
        PackageTypes.Clear();
        foreach (var t in _project.PackageTypes) PackageTypes.Add(t);
        _selectedPackageType = null;
        SelectedPackageType = keep;
        NormalizeSimulatedType();
    }

    /// <summary>Una sola tipologia per simulazione: se la sequenza ne contiene più di una, resta la prima.</summary>
    private void NormalizeSimulatedType()
    {
        var types = _project.PackageTypes;
        var first = _project.Infeed.TypeSequence.Select(id => types.FirstOrDefault(t => t.Id == id)).FirstOrDefault(t => t != null) ?? types.FirstOrDefault();
        if (first != null && (_project.Infeed.TypeSequence.Count != 1 || _project.Infeed.TypeSequence[0] != first.Id))
            _project.Infeed.TypeSequence = new List<string> { first.Id };
        _syncingSimType = true;
        _simPackageType = first;
        _syncingSimType = false;
        Raise(nameof(SimPackageType));
    }

    private int _packagePreviewVersion;
    /// <summary>Incrementato ad ogni modifica del pacco: ridisegna l'anteprima con le legature.</summary>
    public int PackagePreviewVersion { get => _packagePreviewVersion; private set => Set(ref _packagePreviewVersion, value); }

    private int _selectedStrapIndex = -1;
    /// <summary>Legatura selezionata nell'anteprima in pianta (indice dalla testa), -1 se nessuna.</summary>
    public int SelectedStrapIndex { get => _selectedStrapIndex; set => Set(ref _selectedStrapIndex, value); }
    public ICommand AddStrapCommand { get; private set; } = null!;
    public ICommand RemoveStrapCommand { get; private set; } = null!;

    /// <summary>Legature spostate col mouse nell'anteprima: aggiorna progetto e campi.</summary>
    public void OnStrapsEditedInPreview()
    {
        ProjectChanged(structural: false);
        BuildPackageProperties();
    }

    private void AddStrap()
    {
        var p = _selectedPackageType;
        if (p == null) return;
        if (!StrapEditing.TryAdd(p, out var at))
        {
            Status = "Non c'è posto per un'altra legatura: servono le distanze minime tra legature e dalle estremità.";
            return;
        }
        SelectedStrapIndex = p.StrapPositionsMm.IndexOf(at);
        ProjectChanged(structural: false);
        BuildPackageProperties();
        Status = $"Legatura aggiunta a {at:0} mm dalla testa ({p.StrapPositionsMm.Count} legature).";
    }

    private void RemoveStrap()
    {
        var p = _selectedPackageType;
        if (p == null || p.StrapPositionsMm.Count == 0) return;
        int idx = SelectedStrapIndex >= 0 && SelectedStrapIndex < p.StrapPositionsMm.Count ? SelectedStrapIndex : p.StrapPositionsMm.Count - 1;
        StrapEditing.Remove(p, idx);
        SelectedStrapIndex = -1;
        ProjectChanged(structural: false);
        BuildPackageProperties();
        Status = $"Legatura tolta ({p.StrapPositionsMm.Count} legature).";
    }

    private void BuildPackageProperties()
    {
        PackagePreviewVersion++;
        PackageProperties.Clear();
        if (_selectedPackageType == null) return;
        foreach (var item in PropertyItemViewModel.For(_selectedPackageType, OnPackageEdited, IdOptions))
            PackageProperties.Add(item);
    }

    private void OnPackageEdited(PropertyItemViewModel item)
    {
        var sel = _selectedPackageType;
        if (sel != null && item.Name == nameof(PackageType.StrapPositionsMm)) { StrapEditing.SnapAll(sel); item.Refresh(); }
        bool renamed = item.Name is "Id" or "Name";
        ProjectChanged(structural: false);
        if (renamed)
        {
            // aggiorna l'elenco (l'identificativo compare nell'etichetta)
            int i = PackageTypes.IndexOf(sel!);
            if (i >= 0) { PackageTypes.RemoveAt(i); PackageTypes.Insert(i, sel!); _selectedPackageType = null; SelectedPackageType = sel; }
            if (_simPackageType == sel && item.Name == "Id") NormalizeSimulatedType();
        }
        foreach (var other in PackageProperties) if (other != item) other.Refresh();
        PackagePreviewVersion++;
    }

    private void NewPackage()
    {
        var t = new PackageType { Id = NextId("T", AllComponents()), Name = "Nuova tipologia" };
        t.Name = "Tipologia " + t.Id;
        _project.PackageTypes.Add(t);
        ProjectChanged(structural: true);
        SelectedPackageType = t;
    }

    private void DuplicatePackage()
    {
        var src = _selectedPackageType;
        if (src == null) return;
        var t = new PackageType
        {
            Id = NextId("T", AllComponents()), Name = src.Name + " (copia)", LengthMm = src.LengthMm, WeightKg = src.WeightKg,
            GraphicWidthMm = src.GraphicWidthMm, GraphicHeightMm = src.GraphicHeightMm,
            StrapPositionsMm = new List<double>(src.StrapPositionsMm), MinStrapDistanceFromEndsMm = src.MinStrapDistanceFromEndsMm,
            MinDistanceBetweenStrapsMm = src.MinDistanceBetweenStrapsMm
        };
        _project.PackageTypes.Add(t);
        ProjectChanged(structural: true);
        SelectedPackageType = t;
    }

    private void DeletePackage()
    {
        var t = _selectedPackageType;
        if (t == null || _project.PackageTypes.Count <= 1) return;
        if (MessageBox.Show($"Eliminare la tipologia {t}?", "Conferma", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _project.PackageTypes.Remove(t);
        _project.Infeed.TypeSequence.RemoveAll(id => id == t.Id);
        ProjectChanged(structural: true);
    }

    // ------------------------------------------------------------ Stato apparecchiature

    private void UpdateEquipmentStatus(SimFrame? f)
    {
        var rows = new List<(string Name, string State, bool Alarm)>();
        for (int i = 0; i < _project.Strappers.Count; i++)
        {
            var st = _project.Strappers[i];
            var state = f != null && i < f.StrapperStates.Length ? f.StrapperStates[i] : StrapperState.Idle;
            bool fault = f != null && i < f.StrapperProcessStates.Length && f.StrapperProcessStates[i] == StrapperProcessState.Fault;
            rows.Add(($"{st.Id} Legatrice", fault ? "GUASTO" : state switch
            {
                StrapperState.Preparing => "Preparazione",
                StrapperState.Strapping => "Legatura",
                StrapperState.Returning => "Ritorno",
                _ => "Ferma"
            }, fault));
        }
        if (_project.BundleClamp.IsEnabled) rows.Add(("Stringifascio KNOF", ClampText(f?.BundleClampState ?? ClampState.Open), f?.BundleClampState == ClampState.Fault));
        if (_project.Compactor.IsEnabled) rows.Add(("Compattatore laterale", ClampText(f?.CompactorState ?? ClampState.Open), f?.CompactorState == ClampState.Fault));
        if (_project.Rejector.IsEnabled) rows.Add(("Rigettatrice SMA", (f?.RejectorState ?? RejectorState.Idle) == RejectorState.Idle ? "Ferma" : f!.RejectorState.ToString(), f?.RejectorState == RejectorState.Fault));
        rows.Add(("Stopper", (f?.StopperState ?? StopperState.Free) switch { StopperState.Aligning => "Intestatura", StopperState.Aligned => "Pacco intestato", _ => "Libero" }, false));
        rows.Add(("Trasferitore", (f?.TransferState ?? TransferState.Home) switch
        {
            TransferState.Home => "A riposo",
            TransferState.Lifting => "Sollevamento",
            TransferState.Translating => "Traslazione",
            TransferState.Lowering => "Discesa",
            TransferState.Depositing => "Deposito",
            TransferState.Returning => "Ritorno",
            var other => other.ToString()
        }, false));
        for (int i = 0; i < _project.Scales.Count; i++)
        {
            var sc = _project.Scales[i];
            var ss = f != null && i < f.ScaleStates.Length ? f.ScaleStates[i] : ScaleState.Idle;
            rows.Add(($"{sc.Id} Bilico", ss == ScaleState.Idle ? "Libero" : ss.ToString(), ss == ScaleState.Fault));
        }
        bool same = rows.Count == EquipmentStatus.Count && rows.Select((r, i) => EquipmentStatus[i].Name == r.Name).All(x => x);
        if (!same)
        {
            EquipmentStatus.Clear();
            foreach (var r in rows) EquipmentStatus.Add(new EquipmentStatusRow(r.Name));
        }
        for (int i = 0; i < rows.Count; i++) { EquipmentStatus[i].State = rows[i].State; EquipmentStatus[i].Alarm = rows[i].Alarm; }
    }

    private static string ClampText(ClampState s) => s switch
    {
        ClampState.Open => "Aperto",
        ClampState.Closing => "In chiusura",
        ClampState.Closed => "Chiuso",
        ClampState.Opening => "In apertura",
        _ => "GUASTO"
    };

    // ------------------------------------------------------------ Simulazione

    private bool EnsureEngine()
    {
        if (_engine != null) return true;
        // Preparazione del ciclo sul progetto dell'utente: nomi progressivi, collegamenti, stazione che segue le legatrici,
        // sensori automatici; con le legatrici abilitate ogni pacco deve essere legato (vedi SimulationPreparation).
        var prep = SimulationPreparation.Prepare(_project);
        if (prep.Changed)
        {
            ProjectChanged(structural: true);
            MessageBox.Show("Prima di simulare ho corretto il progetto per ottenere un ciclo funzionante:\n\n" + string.Join("\n", prep.Changes.Take(20).Select(c => "• " + c)),
                "Preparazione del ciclo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        if (prep.HasErrors)
        {
            MessageBox.Show(string.Join("\n\n", prep.Errors.Take(10).Select(e => "• " + e)), "Simulazione non avviabile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var report = ProjectValidator.Validate(_project);
        if (report.HasErrors)
        {
            MessageBox.Show("La configurazione contiene errori:\n\n" + string.Join("\n", report.Errors.Take(15).Select(e => "• " + e.Message)),
                "Simulazione non avviabile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _engine = new SimulationEngine(ProjectSerializer.Clone(_project));
        if (_lineSpeedOverride is double ov) _engine.SetLineSpeed(ov);
        _liveMeasuredPph = null; _lastExitedForPph = -1;
        _eventIndex = 0;
        EventLog.Clear();
        Results = null;
        Raise(nameof(Recorder));
        return true;
    }

    private void Play()
    {
        if (!EnsureEngine()) return;
        if (_engine!.IsFinished) { Status = "Simulazione terminata: usare Reset per ripartire."; return; }
        IsPlaying = true;
        _clock.Restart();
        _lastWall = 0;
        _timer.Start();
    }

    private void Pause()
    {
        if (!IsPlaying) return;
        IsPlaying = false;
        _timer.Stop();
        _clock.Stop();
        UpdateAfterAdvance(final: false);
    }

    private void StopSimulation()
    {
        Pause();
        if (_engine == null) return;
        ComputeResults();
        Status = "Simulazione fermata a " + _engine.Time.ToString("0.0", CultureInfo.CurrentCulture) + " s (risultati parziali).";
    }

    private void ResetSimulation()
    {
        if (IsPlaying) { IsPlaying = false; _timer.Stop(); }
        _engine = null;
        Results = null;
        EventLog.Clear();
        Resources.Clear();
        PackageResults.Clear();
        Summary.Clear();
        _eventIndex = 0;
        _suppressDisplayUpdate = true;
        DisplayTime = 0;
        _suppressDisplayUpdate = false;
        Raise(nameof(Recorder));
        Raise(nameof(MaxTime));
        CyclogramVersion++;
        Scene.ApplyFrame(null);
        Status = "Pronto";
    }

    private void OnTick()
    {
        if (_engine == null || !IsPlaying) return;
        double wall = _clock.Elapsed.TotalSeconds;
        double dWall = Math.Min(0.25, wall - _lastWall);
        _lastWall = wall;
        double target = _engine.Time + dWall * SpeedFactor;
        var sw = Stopwatch.StartNew();
        while (!_engine.IsFinished && _engine.Time < target - 1e-9 && sw.ElapsedMilliseconds < 200) _engine.Step();
        UpdateAfterAdvance(final: false);
        if (_engine.IsFinished)
        {
            IsPlaying = false;
            _timer.Stop();
            ComputeResults();
        }
    }

    private void StepOnce()
    {
        if (!EnsureEngine()) return;
        _engine!.Step();
        UpdateAfterAdvance(final: false);
        Status = $"Passo eseguito: t = {_engine.Time:0.000} s";
        if (_engine.IsFinished) ComputeResults();
    }

    private void NextEvent()
    {
        if (!EnsureEngine()) return;
        var ev = _engine!.StepToNextEvent(includeTrace: true);
        UpdateAfterAdvance(final: false);
        Status = ev != null ? "Evento: " + ev : "Nessun ulteriore evento.";
        if (_engine.IsFinished) ComputeResults();
    }

    private void RunToEnd()
    {
        if (!EnsureEngine()) return;
        Mouse.OverrideCursor = Cursors.Wait;
        try { _engine!.RunToCompletion(); }
        finally { Mouse.OverrideCursor = null; }
        UpdateAfterAdvance(final: true);
        ComputeResults();
    }

    private void UpdateAfterAdvance(bool final)
    {
        if (_engine == null) return;
        Raise(nameof(MaxTime));
        _suppressDisplayUpdate = true;
        DisplayTime = _engine.Time;
        _suppressDisplayUpdate = false;
        var frame = _engine.CaptureFrame();
        Scene.ApplyFrame(frame);
        UpdateEquipmentStatus(frame);
        CyclogramVersion++;
        for (; _eventIndex < _engine.Events.Count; _eventIndex++)
        {
            var e = _engine.Events[_eventIndex];
            if (e.Severity >= EventSeverity.Info) EventLog.Add(e);
        }
        while (EventLog.Count > 5000) EventLog.RemoveAt(0);
        int exited = _engine.Packages.Count(p => p.ExitTimeS != null);
        int rejected = _engine.RejectedPackages;
        int onLine = _engine.Packages.Count - exited - rejected;
        if (final) StepRows = StepCyclogram.Build(_engine.Recorder);
        if (exited != _lastExitedForPph)
        {
            _lastExitedForPph = exited;
            _liveMeasuredPph = exited >= 2 ? _engine.ComputeResults().PackagesPerHour : null;
            RaiseProductivity();
        }
        if (IsPlaying || final)
            Status = $"t = {_engine.Time:0.0} s · pacchi in linea {onLine} · usciti {exited}/{_engine.PackagesToSimulate}"
                     + (rejected > 0 ? $" · rigettati {rejected}" : "") + $" · collisioni {_engine.Collisions}"
                     + (_engine.DeviceAlarms > 0 ? $" · allarmi {_engine.DeviceAlarms}" : "")
                     + (_engine.IsFinished ? " · " + _engine.FinishReason : "");
    }

    /// <summary>Mostra lo stato registrato all'istante t (sincronizzazione ciclogramma ↔ 3D).</summary>
    private void ShowFrameAt(double t)
    {
        if (_engine == null) { Scene.ApplyFrame(null); UpdateEquipmentStatus(null); return; }
        var f = t >= _engine.Time - 1e-6 ? _engine.CaptureFrame() : _engine.Recorder.FrameAt(t);
        Scene.ApplyFrame(f);
        UpdateEquipmentStatus(f);
    }

    private void ComputeResults()
    {
        if (_engine == null) return;
        var r = _engine.ComputeResults();
        Results = r;
        Summary.Clear();
        foreach (var (l, v) in ReportExporter.SummaryRows(r)) Summary.Add(new KeyValuePair<string, string>(l, v));
        Resources.Clear();
        foreach (var x in r.Resources) Resources.Add(x);
        PackageResults.Clear();
        foreach (var x in r.Packages) PackageResults.Add(x);
        Status = $"{r.FinishReason} · {r.PackagesExited} pacchi · " +
                 (r.PackagesPerHour.HasValue ? $"{r.PackagesPerHour:0.0} pacchi/h · {r.TonnesPerHour:0.0} t/h · " : "") +
                 $"collo di bottiglia: {r.BottleneckName}" + (r.HasProblems ? " · ATTENZIONE: anomalie rilevate" : "");
    }

    private void ExportCsv()
    {
        if (_engine == null) return;
        var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = SafeName(_project.Name) + ".csv" };
        if (dlg.ShowDialog() != true) return;
        var r = Results ?? _engine.ComputeResults();
        var files = ReportExporter.ExportCsv(r, _engine, Path.GetDirectoryName(dlg.FileName)!, Path.GetFileNameWithoutExtension(dlg.FileName));
        Status = $"Esportati {files.Count} file CSV in {Path.GetDirectoryName(dlg.FileName)}";
    }

    private void ExportExcel()
    {
        if (_engine == null) return;
        var dlg = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = SafeName(_project.Name) + ".xlsx" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            ReportExporter.ExportExcel(Results ?? _engine.ComputeResults(), _engine, dlg.FileName);
            Status = "Report Excel salvato: " + dlg.FileName;
        }
        catch (Exception ex) { MessageBox.Show("Esportazione non riuscita:\n" + ex.Message, "Errore", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}

/// <summary>Riga modificabile dell'elenco sezioni del trasferitore.</summary>
public sealed class TransferSectionRow : ObservableObject
{
    private string _name = "Sezione";
    private double _lengthMm = 3000;
    public string Name { get => _name; set => Set(ref _name, value); }
    public double LengthMm { get => _lengthMm; set => Set(ref _lengthMm, value); }
}
