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
    private ICommand? _exportControlLogicCommand, _exportFunctionalSpecCommand, _optimizeCommandZonesCommand;
    private bool _optimizing;

    public ICommand ExportControlLogicCommand => _exportControlLogicCommand ??= new RelayCommand(ExportControlLogic);
    public ICommand ExportFunctionalSpecCommand => _exportFunctionalSpecCommand ??= new RelayCommand(ExportFunctionalSpec);
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
