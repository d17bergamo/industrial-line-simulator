using System.Globalization;
using LineSimulator.Core;
using LineSimulator.Engine.Runtime;
using LineSimulator.Models;

namespace LineSimulator.Engine;

public sealed record ResourceUsage(
    string Id,
    string Name,
    string Kind,
    double BusyTimeS,
    double Utilization,
    double IdleTimeS,
    double BlockedTimeS,
    int Count,
    double BusyPerPackageS);

public sealed record PackageResult(
    string Id,
    string TypeId,
    double LengthMm,
    double WeightKg,
    double EntryTimeS,
    double? ExitTimeS,
    double? LeadTimeS,
    double WaitingTimeS,
    int AccumulationStops,
    StrapStatus StrapStatus,
    int StrapsDone,
    int StrapsTotal,
    bool Rejected = false,
    double? MeasuredWeightKg = null,
    string? ScaleId = null);

public sealed record TheoreticalZoneResult(
    string ZoneId,
    double LengthM,
    double HighSpeed,
    double TraversalTimeS,
    double PackagesPerHour,
    double TonnesPerHour);

/// <summary>Risultati di una simulazione. I valori "teorici" sono separati da quelli effettivi.</summary>
public sealed class SimulationResults
{
    public string FinishReason { get; init; } = string.Empty;
    public bool Completed { get; init; }
    public double SimulatedTimeS { get; init; }
    public int PackagesRequested { get; init; }
    public int PackagesEntered { get; init; }
    public int PackagesExited { get; init; }

    public double? CycleTimeMeanS { get; init; }

    /// <summary>Istante dell'uscita da cui il ciclo è a regime (null se non determinato).</summary>

    public double? SteadyStateFromS { get; init; }

    /// <summary>Tempi ciclo usati per la media a regime.</summary>

    public int SteadyStateCycles { get; init; }
    public double? CycleTimeMinS { get; init; }
    public double? CycleTimeMaxS { get; init; }
    /// <summary>Produttività effettiva a regime = 3600 / tempo ciclo medio.</summary>
    public double? PackagesPerHour { get; init; }
    public double? TonnesPerHour { get; init; }
    /// <summary>Produttività media sull'intera prova (dal primo ingresso all'ultima uscita).</summary>
    public double? OverallPackagesPerHour { get; init; }
    public double? MeanLeadTimeS { get; init; }
    public double TotalWaitingS { get; init; }
    public double? MeanWaitingS { get; init; }
    public double InfeedWaitingS { get; init; }

    public int Collisions { get; init; }
    public int SafetyViolations { get; init; }
    public int SlipEvents { get; init; }
    public int ExcessiveImpacts { get; init; }
    public int PositioningErrors { get; init; }
    public int ZoneOverfills { get; init; }
    public int TransferInterferences { get; init; }
    public bool Deadlock { get; init; }
    public int AccumulationStops { get; init; }
    public int MaxSimultaneousAccumulated { get; init; }
    public int UnstrappedPackages { get; init; }
    public int RejectedPackages { get; init; }
    public int WeighedPackages { get; init; }
    public int DeviceAlarms { get; init; }
    public int InterlockEvents { get; init; }

    public List<ResourceUsage> Resources { get; init; } = new();
    public List<PackageResult> Packages { get; init; } = new();
    public List<TheoreticalZoneResult> Theoretical { get; init; } = new();
    public double TheoreticalPackagesPerHour { get; init; }
    public double TheoreticalTonnesPerHour { get; init; }

    public string BottleneckId { get; init; } = string.Empty;
    public string BottleneckName { get; init; } = string.Empty;
    public string BottleneckExplanation { get; init; } = string.Empty;
    public List<string> StrategyReports { get; init; } = new();
    public List<SimEvent> Problems { get; init; } = new();

    public bool HasProblems => Collisions > 0 || Deadlock || PositioningErrors > 0 || UnstrappedPackages > 0 || ZoneOverfills > 0 || DeviceAlarms > 0;
}

public static class ResultsCalculator
{
    /// <summary>
    /// Intervallo dei tempi ciclo "a regime" [inizio, fine): la media mobile su una finestra (almeno 3 cicli o la lunghezza della
    /// sequenza di tipologie) entro il 3% della media mobile di riferimento (mediana della seconda metà). Con pochi cicli si scarta
    /// solo il primo (riempimento della linea).
    /// </summary>
    public static (int Start, int End) SteadyWindow(IReadOnlyList<double> cycles, int sequenceLength)
    {
        int n = cycles.Count;
        int w = Math.Max(3, sequenceLength);
        if (n < 3 * w) return n >= 3 ? (1, n) : (0, n);
        var means = new List<double>();
        for (int i = 0; i + w <= n; i++) means.Add(cycles.Skip(i).Take(w).Average());
        var half = means.Skip(means.Count / 2).OrderBy(x => x).ToList();
        double reference = half[half.Count / 2];
        bool Ok(double m) => Math.Abs(m - reference) <= 0.03 * reference;
        int start = 0;
        while (start < means.Count - 1 && !Ok(means[start])) start++;
        int last = means.Count - 1;
        while (last > start && !Ok(means[last])) last--;
        return (start, Math.Max(start + 1, last + w));
    }

    public static SimulationResults Compute(SimulationEngine e)
    {
        var ci = CultureInfo.InvariantCulture;
        var exited = e.Packages.Where(p => p.ExitTimeS.HasValue).OrderBy(p => p.ExitTimeS!.Value).ToList();
        var cycles = new List<double>();
        for (int i = 1; i < exited.Count; i++) cycles.Add(exited[i].ExitTimeS!.Value - exited[i - 1].ExitTimeS!.Value);

        // produttività a regime: si scartano i tempi ciclo dell'avvio (linea che si riempie) e della fine (linea che si svuota)
        var (rs, re) = SteadyWindow(cycles, Math.Max(1, e.Project.Infeed.TypeSequence.Count));
        var steady = cycles.Skip(rs).Take(re - rs).ToList();
        var steadyPkgs = exited.Skip(rs + 1).Take(re - rs).ToList();
        double? mean = steady.Count > 0 ? steady.Average() : cycles.Count > 0 ? cycles.Average() : null;
        double meanWeight = steadyPkgs.Count > 0 ? steadyPkgs.Average(p => p.WeightKg) : exited.Count > 0 ? exited.Average(p => p.WeightKg) : 0;
        double? steadyFrom = steady.Count > 0 && rs + 1 < exited.Count ? exited[rs].ExitTimeS : null;
        double? pph = mean is > 0 ? 3600.0 / mean.Value : null;
        double? tph = pph.HasValue ? pph.Value * meanWeight / 1000.0 : null;

        double firstEntry = e.Packages.Count > 0 ? e.Packages.Min(p => p.EntryTimeS) : 0;
        double? overall = exited.Count > 0 && exited[^1].ExitTimeS > firstEntry
            ? exited.Count * 3600.0 / (exited[^1].ExitTimeS!.Value - firstEntry) : null;

        double span = Math.Max(1e-9, (exited.Count > 0 ? exited[^1].ExitTimeS!.Value : e.Time) - firstEntry);
        int processed = Math.Max(1, exited.Count);

        var resources = new List<ResourceUsage>();
        foreach (var z in e.Zones)
        {
            double busy = z.IsSinglePackage ? z.ReservedTimeS - z.BlockedTimeS : z.OccupiedTimeS - z.BlockedTimeS;
            resources.Add(new ResourceUsage(z.Id, z.Config.Name, z.Info.Kind switch
            {
                ZoneKind.Strapping => "Zona legatura",
                ZoneKind.Stopper => "Zona stopper",
                _ => "Zona trasporto"
            }, busy, busy / span, Math.Max(0, span - z.OccupiedTimeS), z.BlockedTimeS,
                z.IsSinglePackage ? z.Reservations : processed, busy / Math.Max(1, z.IsSinglePackage ? z.Reservations : processed)));
        }
        foreach (var s in e.Strappers)
            resources.Add(new ResourceUsage(s.Id, s.Config.Name, "Legatrice", s.BusyTimeS, s.BusyTimeS / span,
                Math.Max(0, span - s.BusyTimeS), 0, s.CompletedCycles, s.BusyTimeS / processed));
        if (e.Station != null)
            resources.Add(new ResourceUsage("STATION", "Stazione di legatura", "Stazione", e.Station.BusyTimeS, e.Station.BusyTimeS / span,
                Math.Max(0, span - e.Station.BusyTimeS - e.Station.BlockedTimeS), e.Station.BlockedTimeS, e.Station.PackagesProcessed,
                e.Station.BusyTimeS / Math.Max(1, e.Station.PackagesProcessed)));
        resources.Add(new ResourceUsage(e.Project.Stopper.Id, e.Project.Stopper.Name, "Stopper", e.Stopper.OccupiedTimeS, e.Stopper.OccupiedTimeS / span,
            Math.Max(0, span - e.Stopper.OccupiedTimeS), 0, e.Stopper.Packages, e.Stopper.OccupiedTimeS / Math.Max(1, e.Stopper.Packages)));
        resources.Add(new ResourceUsage(e.Project.Transfer.Id, e.Project.Transfer.Name, "Trasferitore", e.Transfer.BusyTimeS, e.Transfer.BusyTimeS / span,
            Math.Max(0, span - e.Transfer.BusyTimeS), 0, e.Transfer.Cycles, e.Transfer.BusyTimeS / Math.Max(1, e.Transfer.Cycles)));
        foreach (var (c, kind) in new[] { (e.BundleClamp, "Stringifascio"), (e.Compactor, "Compattatore") })
            if (c != null)
                resources.Add(new ResourceUsage(c.Id, c.Config.Name, kind, c.BusyTimeS, c.BusyTimeS / span, Math.Max(0, span - c.BusyTimeS), 0,
                    c.Cycles, c.BusyTimeS / Math.Max(1, c.Cycles)));
        if (e.Rejector != null)
            resources.Add(new ResourceUsage(e.Rejector.Id, e.Rejector.Config.Name, "Rigettatrice", e.Rejector.BusyTimeS, e.Rejector.BusyTimeS / span,
                Math.Max(0, span - e.Rejector.BusyTimeS), 0, e.Rejector.Cycles, e.Rejector.BusyTimeS / Math.Max(1, e.Rejector.Cycles)));
        foreach (var sc in e.Scales)
            resources.Add(new ResourceUsage(sc.Id, sc.Config.Name, "Bilico", sc.BusyTimeS, sc.BusyTimeS / span,
                Math.Max(0, span - sc.BusyTimeS), 0, Math.Max(sc.Weighings, sc.Cycles), sc.BusyTimeS / Math.Max(1, Math.Max(sc.Weighings, sc.Cycles))));

        // Collo di bottiglia: risorsa con il maggior impegno per pacco (escluso il tempo bloccato da valle),
        // confrontato con l'intervallo di alimentazione.
        // Dispositivi che lavorano solo su alcuni pacchi (rigettatrice) o in parallelo (bilici, stringifascio,
        // compattatore: già inclusi nel tempo stazione) non sono candidati.
        var candidates = resources.Where(r => r.Kind is not ("Legatrice" or "Rigettatrice" or "Bilico" or "Stringifascio" or "Compattatore") && r.Count > 0).ToList();
        string bnId = "", bnName = "", bnText = "";
        if (candidates.Count > 0 && exited.Count > 0)
        {
            var ordered = candidates.OrderByDescending(r => r.BusyPerPackageS).ToList();
            var top = ordered[0];
            // L'intervallo di ingresso vale solo con la regola "a intervallo fisso": con "un pacco alla volta in legatura"
            // (predefinita) non è usato e non può essere il collo di bottiglia
            double feed = e.Project.Infeed.IntervalS;
            if (e.Project.Infeed.Mode == InfeedMode.FixedInterval && feed >= top.BusyPerPackageS)
            {
                bnId = "INFEED"; bnName = "Alimentazione pacchi";
                bnText = $"La linea è limitata dall'arrivo dei pacchi (intervallo {feed.ToString("0.0", ci)} s) più che da una macchina: " +
                         $"la risorsa più impegnata ({top.Name}) richiede {top.BusyPerPackageS.ToString("0.0", ci)} s/pacco.";
            }
            else
            {
                bnId = top.Id; bnName = top.Name;
                bnText = $"{top.Name} ({top.Kind}) impegna in media {top.BusyPerPackageS.ToString("0.0", ci)} s per pacco" +
                         (mean.HasValue ? $", contro un tempo ciclo medio di {mean.Value.ToString("0.0", ci)} s" : "") +
                         $" (utilizzo {(top.Utilization * 100).ToString("0", ci)}%).";
                if (ordered.Count > 1)
                    bnText += " Seguono: " + string.Join(", ", ordered.Skip(1).Take(3).Select(r => $"{r.Name} {r.BusyPerPackageS.ToString("0.0", ci)} s")) + ".";
                if (top.Kind == "Zona legatura" || top.Kind == "Stazione")
                    bnText += " Interventi possibili: legature in parallelo (distanza legatrici = distanza legature), velocità di posizionamento, zona di legatura più corta.";
                else if (top.Kind == "Zona stopper" || top.Kind == "Trasferitore" || top.Kind == "Stopper")
                    bnText += " Interventi possibili: ridurre il ciclo del trasferitore, consentire l'ingresso prima del ritorno (se sicuro), anticipare il rallentamento.";
            }
        }

        var theo = new List<TheoreticalZoneResult>();
        double refLen = exited.Count > 0 ? exited.Average(p => p.LengthM)
            : e.Project.PackageTypes.Count > 0 ? e.Project.PackageTypes[0].LengthMm / 1000.0 : 1;
        double refWeight = exited.Count > 0 ? meanWeight : e.Project.PackageTypes.Count > 0 ? e.Project.PackageTypes[0].WeightKg : 0;
        foreach (var z in e.Zones)
        {
            double p = TheoreticalProductivity.PackagesPerHour(z.Config.HighSpeed, refLen);
            theo.Add(new TheoreticalZoneResult(z.Id, z.Info.LengthM, z.Config.HighSpeed,
                Kinematics.TraversalTime(z.Info.LengthM, z.Config.HighSpeed), p, TheoreticalProductivity.TonnesPerHour(p, refWeight)));
        }
        double theoPph = theo.Count > 0 ? theo.Min(x => x.PackagesPerHour) : 0;

        var problems = e.Events.Where(x => x.Severity >= EventSeverity.Warning).ToList();
        var waits = e.Packages.Select(p => p.WaitingTimeS).ToList();

        return new SimulationResults
        {
            FinishReason = e.FinishReason ?? "In corso",
            Completed = e.CompletedNormally,
            SimulatedTimeS = e.Time,
            PackagesRequested = e.PackagesToSimulate,
            PackagesEntered = e.Packages.Count,
            PackagesExited = exited.Count,
            CycleTimeMeanS = mean,
            SteadyStateFromS = steadyFrom,
            SteadyStateCycles = steady.Count,
            CycleTimeMinS = cycles.Count > 0 ? cycles.Min() : null,
            CycleTimeMaxS = cycles.Count > 0 ? cycles.Max() : null,
            PackagesPerHour = pph,
            TonnesPerHour = tph,
            OverallPackagesPerHour = overall,
            MeanLeadTimeS = exited.Count > 0 ? exited.Average(p => p.LeadTimeS!.Value) : null,
            TotalWaitingS = waits.Sum(),
            MeanWaitingS = waits.Count > 0 ? waits.Average() : null,
            InfeedWaitingS = e.InfeedWaitingS,
            Collisions = e.Collisions,
            SafetyViolations = e.SafetyViolations,
            SlipEvents = e.SlipEvents,
            ExcessiveImpacts = e.ExcessiveImpacts,
            PositioningErrors = e.PositioningErrors,
            ZoneOverfills = e.ZoneOverfills,
            TransferInterferences = e.TransferInterferences,
            Deadlock = e.Deadlocked,
            AccumulationStops = e.AccumulationStops,
            MaxSimultaneousAccumulated = e.MaxSimultaneousAccumulated,
            UnstrappedPackages = e.Packages.Count(p => p.StrapStatus == StrapStatus.Failed || (p.ExitTimeS.HasValue && p.StrapStatus == StrapStatus.Pending)),
            RejectedPackages = e.RejectedPackages,
            WeighedPackages = e.WeighedPackages,
            DeviceAlarms = e.DeviceAlarms,
            InterlockEvents = e.InterlockEvents,
            Resources = resources,
            Packages = e.Packages.Select(p => new PackageResult(p.Id, p.Type.Id, p.Type.LengthMm, p.WeightKg, p.EntryTimeS, p.ExitTimeS,
                p.LeadTimeS, p.WaitingTimeS, p.AccumulationStops, p.StrapStatus, p.StrapsDone, p.StrapsTotal,
                p.State == PackageState.Rejected, p.MeasuredWeightKg, p.ScaleId)).ToList(),
            Theoretical = theo,
            TheoreticalPackagesPerHour = theoPph,
            TheoreticalTonnesPerHour = TheoreticalProductivity.TonnesPerHour(theoPph, refWeight),
            BottleneckId = bnId,
            BottleneckName = bnName,
            BottleneckExplanation = bnText,
            StrategyReports = e.StrappingPlans.Values.Select(c => $"Tipologia {c.PackageTypeId}:{Environment.NewLine}{c.SelectionReason}").ToList(),
            Problems = problems
        };
    }
}
