using System.IO;
using System.Windows;
using System.Windows.Input;
using LineSimulator.App.Mvvm;
using LineSimulator.Infrastructure;
using Microsoft.Win32;

namespace LineSimulator.App.ViewModels;

/// <summary>
/// Output dell'ottimizzazione (file separato per non interferire con il resto del view model):
/// logica di controllo AND/OR/NOT, specifica tecnica funzionale in Word (solo su richiesta),
/// ottimizzazione delle zone di comando (numero minimo di inverter).
/// </summary>
public sealed partial class MainViewModel
{
    private ICommand? _exportControlLogicCommand, _exportFunctionalSpecCommand, _optimizeCommandZonesCommand, _reachTargetCommand;
    private bool _optimizing;

    /// <summary>Obiettivo di produttività (t/h) della simulazione.</summary>
    public double SimTargetTph
    {
        get => _project.Simulation.TargetTonnesPerHour;
        set { if (Math.Abs(_project.Simulation.TargetTonnesPerHour - value) < 1e-9) return; _project.Simulation.TargetTonnesPerHour = Math.Max(0, value); Raise(nameof(SimTargetTph)); _modified = true; }
    }

    public ICommand ReachTargetCommand => _reachTargetCommand ??= new RelayCommand(ReachTarget, () => !_optimizing);

    /// <summary>
    /// Raggiungi l'obiettivo: simula, individua il collo di bottiglia e prova le modifiche ammesse (layout e macchine,
    /// logiche), poi propone il percorso migliore; con il consenso dell'utente lo applica al progetto.
    /// </summary>
    private async void ReachTarget()
    {
        if (_optimizing) return;
        if (_project.Simulation.TargetTonnesPerHour <= 0)
        {
            MessageBox.Show("Imposta prima l'obiettivo di produttività in tonnellate/ora (casella \"Obiettivo t/h\").", "Obiettivo di produttività",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _optimizing = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var snapshot = ProjectSerializer.Clone(_project);
            if (_lineSpeedOverride is double ov)                       // stessa velocità di linea della simulazione mostrata
                foreach (var s in snapshot.Sections) { s.HighSpeed = ov; s.LowSpeed = Math.Min(s.LowSpeed, ov); }
            double target = _project.Simulation.TargetTonnesPerHour;
            var progress = new Progress<string>(s => Status = "Obiettivo di produttività: " + s);
            var report = await Task.Run(() => ProductivityAdvisor.Analyze(snapshot, target, progress: progress));
            if (report.BestProject == null || report.Path.Count == 0)
            {
                if (report.SavingProject != null)
                {
                    // linea oltre l'obiettivo di più del 25%: proposta di unire lo svincolo all'inverter di legatura
                    if (MessageBox.Show(report.Summary + "\nApplicare la proposta di risparmio (un inverter in meno)?", "Obiettivo di produttività",
                                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        LoadProject(report.SavingProject, _filePath);
                        _modified = true;
                        Raise(nameof(SimTargetTph));
                        Status = $"Svincolo unito all'inverter di legatura: {report.Saving!.TonnesPerHour:0.0} t/h (obiettivo {target:0.0} t/h).";
                    }
                    else Status = "Obiettivo di produttività: analisi completata, progetto invariato.";
                    return;
                }
                MessageBox.Show(report.Summary, "Obiettivo di produttività", MessageBoxButton.OK,
                    report.TargetReached ? MessageBoxImage.Information : MessageBoxImage.Warning);
                Status = "Obiettivo di produttività: nessuna modifica applicata.";
                return;
            }
            var answer = MessageBox.Show(report.Summary + "\nApplicare al progetto le modifiche del percorso proposto?",
                "Obiettivo di produttività", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                var path = _filePath;
                bool saving = report.SavingProject != null
                              && MessageBox.Show("Con il percorso applicato la linea supera l'obiettivo di oltre il 25%.\n" + report.Saving!.Description +
                                                 "\nUnire anche lo svincolo all'inverter di legatura (un inverter in meno)?", "Obiettivo di produttività",
                                                 MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                LoadProject(saving ? report.SavingProject! : report.BestProject, path);
                _modified = true;
                Raise(nameof(SimTargetTph));
                Status = $"Applicate {report.Path.Count + (saving ? 1 : 0)} modifiche: {(saving ? report.Saving!.TonnesPerHour : report.BestTonnesPerHour):0.0} t/h (obiettivo {target:0.0} t/h).";
            }
            else Status = "Obiettivo di produttività: analisi completata, progetto invariato.";
        }
        catch (Exception ex) { MessageBox.Show("Analisi non riuscita:\n" + ex.Message, "Errore", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            _optimizing = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public ICommand ExportControlLogicCommand => _exportControlLogicCommand ??= new RelayCommand(ExportControlLogic);
    public ICommand ExportFunctionalSpecCommand => _exportFunctionalSpecCommand ??= new RelayCommand(ExportFunctionalSpec);
    private ICommand? _exportTopViewCommand;
    public ICommand ExportTopViewPdfCommand => _exportTopViewCommand ??= new RelayCommand(ExportTopViewPdf);
    public ICommand OptimizeCommandZonesCommand => _optimizeCommandZonesCommand ??= new RelayCommand(OptimizeCommandZones, () => !_optimizing);

    private void ExportControlLogic()
    {
        var dlg = new SaveFileDialog { Filter = "Logica di controllo (*.json)|*.json", FileName = SafeName(_project.Name) + "_logica.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var logic = ControlLogicGenerator.Generate(_project);
            var files = ControlLogicGenerator.Export(logic, dlg.FileName);
            Status = $"Logica di controllo esportata: {logic.Rules.Count} regole, {logic.InverterCount} inverter, {logic.PhotocellCount} fotocellule ({files.Count} file in {Path.GetDirectoryName(dlg.FileName)})";
        }
        catch (Exception ex) { MessageBox.Show("Esportazione non riuscita:\n" + ex.Message, "Errore", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ExportFunctionalSpec()
    {
        var dlg = new SaveFileDialog { Filter = "Documento Word (*.docx)|*.docx", FileName = SafeName(_project.Name) + "_specifica_funzionale.docx" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            FunctionalSpecExporter.Export(_project, Results ?? _engine?.ComputeResults(), dlg.FileName);
            Status = "Specifica tecnica funzionale salvata: " + dlg.FileName;
        }
        catch (Exception ex) { MessageBox.Show("Esportazione non riuscita:\n" + ex.Message, "Errore", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    /// <summary>PDF della vista dall'alto: sezioni inverter a colori, motori commutabili, quote tra fotocellule, fermi e legatrici.</summary>
    private ICommand? _exportPrintCyclogramCommand;
    /// <summary>Ciclogramma da stampare in bianco e nero (A3 orizzontale) del ciclo a regime.</summary>
    public ICommand ExportPrintCyclogramCommand => _exportPrintCyclogramCommand ??= new RelayCommand(ExportPrintCyclogram);

    private void ExportPrintCyclogram()
    {
        var dlg = new SaveFileDialog { Filter = "PDF (*.pdf)|*.pdf", FileName = SafeName(_project.Name) + "_ciclogramma.pdf" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            CyclogramPrintPdf.Save(dlg.FileName, _project, _lineSpeedOverride);
            Status = "Ciclogramma da stampare esportato: " + dlg.FileName;
        }
        catch (Exception ex) { MessageBox.Show("Esportazione non riuscita:\n" + ex.Message, "Errore", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { Mouse.OverrideCursor = null; }
    }

    private void ExportTopViewPdf()
    {
        var dlg = new SaveFileDialog { Filter = "PDF (*.pdf)|*.pdf", FileName = SafeName(_project.Name) + "_vista_dall_alto.pdf" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            LayoutTopViewPdf.Save(dlg.FileName, _project);
            Status = "Vista dall'alto esportata: " + dlg.FileName;
        }
        catch (Exception ex) { MessageBox.Show("Esportazione non riuscita:\n" + ex.Message, "Errore", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void OptimizeCommandZones()
    {
        if (_optimizing) return;
        _optimizing = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var snapshot = ProjectSerializer.Clone(_project);
            var progress = new Progress<string>(s => Status = "Ottimizzazione zone di comando: " + s);
            var report = await Task.Run(() => CommandZoneOptimizer.Optimize(snapshot, progress: progress));
            if (report.Best == null)
            {
                MessageBox.Show(report.Explanation, "Ottimizzazione zone di comando", MessageBoxButton.OK, MessageBoxImage.Warning);
                Status = "Ottimizzazione: nessuna suddivisione ammessa.";
                return;
            }
            var answer = MessageBox.Show(report.Explanation + "\n\nApplicare la suddivisione migliore al progetto?",
                "Ottimizzazione zone di comando", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                CommandZoneOptimizer.Apply(_project, report.Best);
                ProjectChanged(structural: true);
                Status = $"Applicata la suddivisione con {report.Best.Inverters} inverter: {report.Best.Label}";
            }
            else Status = "Ottimizzazione completata, progetto invariato.";
        }
        catch (Exception ex) { MessageBox.Show("Ottimizzazione non riuscita:\n" + ex.Message, "Errore", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            _optimizing = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
