using System.Collections.ObjectModel;
using LineSimulator.App.Mvvm;
using LineSimulator.Core;
using LineSimulator.Models;

namespace LineSimulator.App.ViewModels;

/// <summary>Scelta di una tendina: le scelte non possibili restano in elenco ma non si selezionano.</summary>
public sealed record ChoiceOption(object Value, string Label, bool IsEnabled, string Hint);

/// <summary>
/// Riga della tabella "Configurazioni dei pacchi": peso, lunghezza (m), numero di legature, base e altezza (mm),
/// single/double banking e intestatura di testa/coda. Le scelte non possibili per la linea sono disabilitate.
/// </summary>
public sealed class PackageRowViewModel : ObservableObject
{
    private readonly Func<LineProject> _project;
    private readonly Action<PackageRowViewModel, string> _edited;

    public PackageRowViewModel(PackageType type, Func<LineProject> project, Action<PackageRowViewModel, string> edited)
    {
        Type = type;
        _project = project;
        _edited = edited;
    }

    public PackageType Type { get; }
    public string Id => Type.Id;

    public double WeightKg
    {
        get => Type.WeightKg;
        set { if (value <= 0 || Math.Abs(value - Type.WeightKg) < 1e-9) return; Type.WeightKg = value; _edited(this, nameof(PackageType.WeightKg)); }
    }

    /// <summary>Lunghezza in metri (nel modello in mm), a passi di 50 mm.</summary>
    public double LengthM
    {
        get => Type.LengthMm / 1000.0;
        set
        {
            double mm = Math.Round(value * 1000 / 50) * 50;
            if (mm < 500 || Math.Abs(mm - Type.LengthMm) < 0.5) { Raise(); return; }
            _edited(this, "Length:" + mm.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public int StrapCount
    {
        get => Type.StrapPositionsMm.Count;
        set { if (value < 0 || value == Type.StrapPositionsMm.Count) return; _edited(this, "Straps:" + value); }
    }

    public double WidthMm
    {
        get => Type.GraphicWidthMm;
        set { if (value <= 0 || Math.Abs(value - Type.GraphicWidthMm) < 1e-9) return; Type.GraphicWidthMm = value; _edited(this, nameof(PackageType.GraphicWidthMm)); }
    }

    public double HeightMm
    {
        get => Type.GraphicHeightMm;
        set { if (value <= 0 || Math.Abs(value - Type.GraphicHeightMm) < 1e-9) return; Type.GraphicHeightMm = value; _edited(this, nameof(PackageType.GraphicHeightMm)); }
    }

    public IReadOnlyList<ChoiceOption> BankingChoices
    {
        get
        {
            var p = _project();
            bool dbl = BankingOptions.DoubleBankingAvailable(p, Type);
            return new[]
            {
                new ChoiceOption(AccumulationMode.SingleBanking, "Single banking", true, "Un pacco per ciclo del trasferitore"),
                new ChoiceOption(AccumulationMode.DoubleBanking, "Double banking", dbl,
                    dbl ? "Due pacchi per ciclo del trasferitore" : "Non possibile: due pacchi di questa lunghezza non stanno sul trasferitore")
            };
        }
    }

    public AccumulationMode Banking
    {
        get => BankingOptions.Banking(_project(), Type);
        set
        {
            if (value == AccumulationMode.DoubleBanking && !BankingOptions.DoubleBankingAvailable(_project(), Type)) { Raise(); return; }
            if (Type.Banking == value) return;
            Type.Banking = value;
            // l'intestatura scelta resta se è ancora possibile, altrimenti si sposta su quella possibile
            if (Type.Heading is { } h && !BankingOptions.HeadingAvailable(_project(), Type, value, h)) Type.Heading = BankingOptions.Heading(_project(), Type);
            _edited(this, nameof(PackageType.Banking));
        }
    }

    public IReadOnlyList<ChoiceOption> HeadingChoices
    {
        get
        {
            var p = _project();
            var b = BankingOptions.Banking(p, Type);
            bool hasStop = BankingOptions.TailHeadingAvailable(p);
            bool head = BankingOptions.HeadingAvailable(p, Type, b, StopReference.Head);
            bool tail = BankingOptions.HeadingAvailable(p, Type, b, StopReference.Tail);
            string tailHint = tail
                ? (hasStop ? "Il pacco supera il fermo scomparente, il fermo si solleva e il pacco torna indietro" : "Senza fermo: il pacco si ferma per posizionamento con la coda a inizio trasferitore")
                : (hasStop ? "Non possibile: il pacco non ci sta oltre il fermo scomparente" : "Non possibile in double banking senza fermo scomparente");
            return new[]
            {
                new ChoiceOption(StopReference.Head, "Di testa", head, head ? "La testa del pacco in battuta sul fermo" : "Non possibile in double banking con questo pacco"),
                new ChoiceOption(StopReference.Tail, "Di coda", tail, tailHint)
            };
        }
    }

    public StopReference Heading
    {
        get => BankingOptions.Heading(_project(), Type);
        set
        {
            var p = _project();
            if (!BankingOptions.HeadingAvailable(p, Type, BankingOptions.Banking(p, Type), value)) { Raise(); return; }
            if (Type.Heading == value) return;
            Type.Heading = value;
            _edited(this, nameof(PackageType.Heading));
        }
    }

    /// <summary>Aggiorna tutte le colonne (modifiche dalle caratteristiche o dal layout).</summary>
    public void RefreshAll() => Raise(string.Empty);
}

public partial class MainViewModel
{
    /// <summary>Tabella delle configurazioni dei pacchi (schermata Pacchi).</summary>
    public ObservableCollection<PackageRowViewModel> PackageRows { get; } = new();

    private void RebuildPackageRows()
    {
        PackageRows.Clear();
        foreach (var t in _project.PackageTypes) PackageRows.Add(new PackageRowViewModel(t, () => _project, OnPackageRowEdited));
    }

    private void RefreshPackageRows()
    {
        if (PackageRows.Count != _project.PackageTypes.Count || PackageRows.Where((r, i) => r.Type != _project.PackageTypes[i]).Any())
        {
            RebuildPackageRows();
            return;
        }
        foreach (var r in PackageRows) r.RefreshAll();
    }

    private void OnPackageRowEdited(PackageRowViewModel row, string what)
    {
        var t = row.Type;
        bool renamed = false;
        if (what.StartsWith("Length:", StringComparison.Ordinal))
        {
            double old = t.LengthMm;
            t.LengthMm = double.Parse(what["Length:".Length..], System.Globalization.CultureInfo.InvariantCulture);
            // come nelle caratteristiche: le legature seguono la lunghezza in proporzione, l'identificativo segue la lunghezza
            StrapEditing.ScaleToLength(t, old);
            renamed = PackageNaming.Refresh(_project, t);
            t.Name = t.Id;
            if (t == _selectedPackageType) { _packageLengthMm = t.LengthMm; SelectedStrapIndex = -1; }
        }
        else if (what.StartsWith("Straps:", StringComparison.Ordinal))
        {
            int n = int.Parse(what["Straps:".Length..], System.Globalization.CultureInfo.InvariantCulture);
            if (!StrapEditing.TrySetSymmetric(t, n))
            {
                Status = $"{t.Id}: {n} legature non stanno nel pacco con le distanze minime tra legature e dalle estremità.";
                row.RefreshAll();
                return;
            }
            if (t == _selectedPackageType) SelectedStrapIndex = -1;
        }
        // cambiata la lunghezza, banking e intestatura scelti restano solo se ancora possibili
        BankingOptions.Normalize(_project);
        ProjectChanged(structural: false);
        if (renamed)
        {
            int i = PackageTypes.IndexOf(t);
            if (i >= 0) { PackageTypes.RemoveAt(i); PackageTypes.Insert(i, t); if (t == _selectedPackageType) { _selectedPackageType = null; SelectedPackageType = t; } }
            if (_simPackageType == t) NormalizeSimulatedType();
        }
        if (t == _selectedPackageType) BuildPackageProperties();
        RefreshPackageRows();
        Raise(nameof(SimBanking)); Raise(nameof(SimHeading)); Raise(nameof(SimBankingChoices)); Raise(nameof(SimHeadingChoices));
        Status = $"{t.Id}: {DescribeRow(t)}";
    }

    private string DescribeRow(PackageType t) =>
        $"{t.WeightKg:0} kg, {t.LengthMm / 1000.0:0.##} m, {t.StrapPositionsMm.Count} legature, {t.GraphicWidthMm:0} × {t.GraphicHeightMm:0} mm, " +
        $"{(BankingOptions.Banking(_project, t) == AccumulationMode.DoubleBanking ? "double" : "single")} banking, intestatura di " +
        $"{(BankingOptions.Heading(_project, t) == StopReference.Tail ? "coda" : "testa")}";

    // Barra "Cosa simulo": banking e intestatura del pacco simulato (le stesse scelte della tabella)
    private PackageRowViewModel? SimRow => _simPackageType == null ? null : PackageRows.FirstOrDefault(r => r.Type == _simPackageType);
    public IReadOnlyList<ChoiceOption> SimBankingChoices => SimRow?.BankingChoices ?? Array.Empty<ChoiceOption>();
    public IReadOnlyList<ChoiceOption> SimHeadingChoices => SimRow?.HeadingChoices ?? Array.Empty<ChoiceOption>();
}
