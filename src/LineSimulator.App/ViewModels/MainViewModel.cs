using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using LineSimulator.App.Mvvm;
using LineSimulator.App.Rendering;
using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Infrastructure;
using LineSimulator.Models;
using LineSimulator.Optimization;
using Microsoft.Win32;

namespace LineSimulator.App.ViewModels;

public sealed record ExampleItem(string Name, Func<LineProject> Factory);

public sealed class MainViewModel : ObservableObject
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
        SetSpeedCommand = new RelayCommand(p => { if (double.TryParse(p?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) SpeedFactor = s; });

        LoadProject(_project, null);
    }

    // ------------------------------------------------------------ Proprietà bindabili

    public LineScene Scene { get; } = new();
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
        _filePath = path;
        _modified = false;
        _engine = null;
        Results = null;
        Scene.Build(_project);
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
        var selected = SelectedNode?.Model;
        Scene.Build(_project);
        Scene.Highlight(selected);
        if (structural) BuildTree(selected);
        else UpdateTreeHeaders();
        ResetSimulation();
        RefreshValidation();
        RefreshStrategies();
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

    private void BuildProperties()
    {
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
        bool structural = item.Name is "Id" or "StartMm" or "PositionMm" or "InverterId" or "IsEnabled";
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
        or IntermediateStopConfig or WeighingScaleConfig;

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
        }
        _selectedNode = null;
        ProjectChanged(structural: true);
        SelectedNode = Tree.FirstOrDefault();
    }

    // ------------------------------------------------------------ Simulazione

    private bool EnsureEngine()
    {
        if (_engine != null) return true;
        var report = ProjectValidator.Validate(_project);
        if (report.HasErrors)
        {
            MessageBox.Show("La configurazione contiene errori:\n\n" + string.Join("\n", report.Errors.Take(15).Select(e => "• " + e.Message)),
                "Simulazione non avviabile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _engine = new SimulationEngine(ProjectSerializer.Clone(_project));
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
        Scene.ApplyFrame(_engine.CaptureFrame());
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
        if (IsPlaying || final)
            Status = $"t = {_engine.Time:0.0} s · pacchi in linea {onLine} · usciti {exited}/{_engine.PackagesToSimulate}"
                     + (rejected > 0 ? $" · rigettati {rejected}" : "") + $" · collisioni {_engine.Collisions}"
                     + (_engine.DeviceAlarms > 0 ? $" · allarmi {_engine.DeviceAlarms}" : "")
                     + (_engine.IsFinished ? " · " + _engine.FinishReason : "");
    }

    /// <summary>Mostra lo stato registrato all'istante t (sincronizzazione ciclogramma ↔ 3D).</summary>
    private void ShowFrameAt(double t)
    {
        if (_engine == null) { Scene.ApplyFrame(null); return; }
        var f = t >= _engine.Time - 1e-6 ? _engine.CaptureFrame() : _engine.Recorder.FrameAt(t);
        Scene.ApplyFrame(f);
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
