using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using LineSimulator.Engine;

namespace LineSimulator.Infrastructure;

/// <summary>Esportazione dei risultati in CSV (separatore ';', formato numerico italiano) ed Excel.</summary>
public static class ReportExporter
{
    private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    private static string N(double? v, string fmt = "0.00") => v.HasValue && !double.IsInfinity(v.Value) && !double.IsNaN(v.Value) ? v.Value.ToString(fmt, It) : "";

    private static string Esc(string s) => s.Contains(';') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static IReadOnlyList<(string Label, string Value)> SummaryRows(SimulationResults r) => new List<(string, string)>
    {
        ("Esito", r.FinishReason),
        ("Tempo simulato [s]", N(r.SimulatedTimeS, "0.0")),
        ("Pacchi richiesti / entrati / usciti", $"{r.PackagesRequested} / {r.PackagesEntered} / {r.PackagesExited}"),
        ("Tempo ciclo medio [s]", N(r.CycleTimeMeanS)),
        ("Tempo ciclo minimo [s]", N(r.CycleTimeMinS)),
        ("Tempo ciclo massimo [s]", N(r.CycleTimeMaxS)),
        ("Produttività effettiva a regime [pacchi/h]", N(r.PackagesPerHour, "0.0")),
        ("Produttività effettiva a regime [t/h]", N(r.TonnesPerHour, "0.0")),
        ("Produttività media sulla prova [pacchi/h]", N(r.OverallPackagesPerHour, "0.0")),
        ("Produttività teorica senza spaziatura [pacchi/h]", N(r.TheoreticalPackagesPerHour, "0.0")),
        ("Produttività teorica [t/h]", N(r.TheoreticalTonnesPerHour, "0.0")),
        ("Tempo medio di attraversamento [s]", N(r.MeanLeadTimeS, "0.0")),
        ("Tempo di attesa totale pacchi [s]", N(r.TotalWaitingS, "0.0")),
        ("Attesa in ingresso [s]", N(r.InfeedWaitingS, "0.0")),
        ("Arresti per accumulo", r.AccumulationStops.ToString(It)),
        ("Max pacchi accumulati contemporaneamente", r.MaxSimultaneousAccumulated.ToString(It)),
        ("Collisioni", r.Collisions.ToString(It)),
        ("Violazioni distanza minima", r.SafetyViolations.ToString(It)),
        ("Slittamenti", r.SlipEvents.ToString(It)),
        ("Impatti eccessivi allo stopper", r.ExcessiveImpacts.ToString(It)),
        ("Errori di posizionamento", r.PositioningErrors.ToString(It)),
        ("Pacchi non legati", r.UnstrappedPackages.ToString(It)),
        ("Pacchi rigettati (SMA)", r.RejectedPackages.ToString(It)),
        ("Pacchi pesati", r.WeighedPackages.ToString(It)),
        ("Allarmi dispositivi", r.DeviceAlarms.ToString(It)),
        ("Interventi interblocchi", r.InterlockEvents.ToString(It)),
        ("Blocco linea (deadlock)", r.Deadlock ? "SÌ" : "no"),
        ("Collo di bottiglia", r.BottleneckName),
        ("Motivazione", r.BottleneckExplanation)
    };

    public static string SummaryCsv(SimulationResults r)
    {
        var sb = new StringBuilder("Voce;Valore\n");
        foreach (var (l, v) in SummaryRows(r)) sb.Append(Esc(l)).Append(';').Append(Esc(v)).Append('\n');
        return sb.ToString();
    }

    public static string PackagesCsv(SimulationResults r)
    {
        var sb = new StringBuilder("ID;Tipo;Lunghezza mm;Peso kg;Ingresso s;Uscita s;Attraversamento s;Attesa s;Arresti accumulo;Legatura;Legature eseguite;Rigettato;Peso misurato kg;Bilico\n");
        foreach (var p in r.Packages)
            sb.Append(string.Join(";", p.Id, p.TypeId, N(p.LengthMm, "0"), N(p.WeightKg, "0"), N(p.EntryTimeS), N(p.ExitTimeS),
                N(p.LeadTimeS), N(p.WaitingTimeS), p.AccumulationStops.ToString(It), p.StrapStatus.ToString(), $"{p.StrapsDone}/{p.StrapsTotal}",
                p.Rejected ? "SÌ" : "no", N(p.MeasuredWeightKg, "0"), Esc(p.ScaleId ?? ""))).Append('\n');
        return sb.ToString();
    }

    public static string ResourcesCsv(SimulationResults r)
    {
        var sb = new StringBuilder("ID;Risorsa;Tipo;Occupazione s;Utilizzo %;Tempo morto s;Bloccato s;Cicli/pacchi;Impegno per pacco s\n");
        foreach (var x in r.Resources)
            sb.Append(string.Join(";", Esc(x.Id), Esc(x.Name), Esc(x.Kind), N(x.BusyTimeS), N(x.Utilization * 100, "0.0"), N(x.IdleTimeS),
                N(x.BlockedTimeS), x.Count.ToString(It), N(x.BusyPerPackageS))).Append('\n');
        return sb.ToString();
    }

    public static string EventsCsv(IEnumerable<SimEvent> events)
    {
        var sb = new StringBuilder("N;Tempo s;Tipo;Gravità;Componente;Pacco;Messaggio\n");
        foreach (var e in events)
            sb.Append(string.Join(";", e.Sequence.ToString(It), N(e.TimeS, "0.000"), e.Type.ToString(), e.Severity.ToString(),
                Esc(e.ComponentId ?? ""), Esc(e.PackageId ?? ""), Esc(e.Message))).Append('\n');
        return sb.ToString();
    }

    public static string CyclogramCsv(SimulationRecorder rec)
    {
        var sb = new StringBuilder("Utenza;Gruppo;Inizio s;Fine s;Durata s;Stato;Dettaglio\n");
        foreach (var t in rec.Tracks)
            foreach (var s in t.Segments)
                sb.Append(string.Join(";", Esc(t.Label), t.Group.ToString(), N(s.StartS, "0.000"), N(s.EndS, "0.000"),
                    N(s.DurationS, "0.000"), s.State.ToString(), Esc(s.Detail))).Append('\n');
        return sb.ToString();
    }

    /// <summary>Scrive tutti i CSV nella cartella indicata. Restituisce i percorsi creati.</summary>
    public static IReadOnlyList<string> ExportCsv(SimulationResults r, SimulationEngine engine, string folder, string prefix = "risultati")
    {
        Directory.CreateDirectory(folder);
        var enc = new UTF8Encoding(true); // BOM per Excel
        var files = new List<(string Name, string Content)>
        {
            ($"{prefix}_riepilogo.csv", SummaryCsv(r)),
            ($"{prefix}_pacchi.csv", PackagesCsv(r)),
            ($"{prefix}_risorse.csv", ResourcesCsv(r)),
            ($"{prefix}_eventi.csv", EventsCsv(engine.Events)),
            ($"{prefix}_ciclogramma.csv", CyclogramCsv(engine.Recorder))
        };
        var paths = new List<string>();
        foreach (var (name, content) in files)
        {
            string path = Path.Combine(folder, name);
            File.WriteAllText(path, content, enc);
            paths.Add(path);
        }
        return paths;
    }

    public static void ExportExcel(SimulationResults r, SimulationEngine engine, string path) => File.WriteAllBytes(path, ExcelBytes(r, engine));

    /// <summary>Cartella Excel dei risultati in memoria (usata anche dalla versione web).</summary>
    public static byte[] ExcelBytes(SimulationResults r, SimulationEngine engine)
    {
        using var wb = new XLWorkbook();

        var ws = wb.Worksheets.Add("Riepilogo");
        ws.Cell(1, 1).Value = "Industrial Line Simulator - " + engine.Project.Name;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        int row = 3;
        foreach (var (l, v) in SummaryRows(r))
        {
            ws.Cell(row, 1).Value = l;
            ws.Cell(row, 2).Value = v;
            row++;
        }
        ws.Cell(row + 1, 1).Value = "Nota: strumento di progettazione, non sistema di sicurezza certificato. Interblocchi reali da verificare su PLC e circuiti di sicurezza.";
        ws.Column(1).Width = 50;
        ws.Column(2).Width = 80;

        var wp = wb.Worksheets.Add("Pacchi");
        string[] ph = { "ID", "Tipo", "Lunghezza mm", "Peso kg", "Ingresso s", "Uscita s", "Attraversamento s", "Attesa s", "Arresti accumulo", "Legatura", "Legature", "Rigettato", "Peso misurato kg", "Bilico" };
        for (int i = 0; i < ph.Length; i++) wp.Cell(1, i + 1).Value = ph[i];
        row = 2;
        foreach (var p in r.Packages)
        {
            wp.Cell(row, 1).Value = p.Id;
            wp.Cell(row, 2).Value = p.TypeId;
            wp.Cell(row, 3).Value = p.LengthMm;
            wp.Cell(row, 4).Value = p.WeightKg;
            wp.Cell(row, 5).Value = Math.Round(p.EntryTimeS, 3);
            if (p.ExitTimeS.HasValue) wp.Cell(row, 6).Value = Math.Round(p.ExitTimeS.Value, 3);
            if (p.LeadTimeS.HasValue) wp.Cell(row, 7).Value = Math.Round(p.LeadTimeS.Value, 3);
            wp.Cell(row, 8).Value = Math.Round(p.WaitingTimeS, 3);
            wp.Cell(row, 9).Value = (double)p.AccumulationStops;
            wp.Cell(row, 10).Value = p.StrapStatus.ToString();
            wp.Cell(row, 11).Value = $"{p.StrapsDone}/{p.StrapsTotal}";
            wp.Cell(row, 12).Value = p.Rejected ? "SÌ" : "no";
            if (p.MeasuredWeightKg.HasValue) wp.Cell(row, 13).Value = p.MeasuredWeightKg.Value;
            wp.Cell(row, 14).Value = p.ScaleId ?? "";
            row++;
        }

        var wr = wb.Worksheets.Add("Risorse");
        string[] rh = { "ID", "Risorsa", "Tipo", "Occupazione s", "Utilizzo %", "Tempo morto s", "Bloccato s", "Cicli", "Impegno per pacco s" };
        for (int i = 0; i < rh.Length; i++) wr.Cell(1, i + 1).Value = rh[i];
        row = 2;
        foreach (var x in r.Resources)
        {
            wr.Cell(row, 1).Value = x.Id;
            wr.Cell(row, 2).Value = x.Name;
            wr.Cell(row, 3).Value = x.Kind;
            wr.Cell(row, 4).Value = Math.Round(x.BusyTimeS, 2);
            wr.Cell(row, 5).Value = Math.Round(x.Utilization * 100, 1);
            wr.Cell(row, 6).Value = Math.Round(x.IdleTimeS, 2);
            wr.Cell(row, 7).Value = Math.Round(x.BlockedTimeS, 2);
            wr.Cell(row, 8).Value = (double)x.Count;
            wr.Cell(row, 9).Value = Math.Round(x.BusyPerPackageS, 2);
            row++;
        }

        var wt = wb.Worksheets.Add("Teorico");
        string[] th = { "Zona", "Lunghezza m", "Velocità alta m/s", "t = L/v s", "Pacchi/h teorici", "t/h teoriche" };
        for (int i = 0; i < th.Length; i++) wt.Cell(1, i + 1).Value = th[i];
        row = 2;
        foreach (var z in r.Theoretical)
        {
            wt.Cell(row, 1).Value = z.ZoneId;
            wt.Cell(row, 2).Value = Math.Round(z.LengthM, 3);
            wt.Cell(row, 3).Value = z.HighSpeed;
            wt.Cell(row, 4).Value = Math.Round(z.TraversalTimeS, 2);
            wt.Cell(row, 5).Value = Math.Round(z.PackagesPerHour, 1);
            wt.Cell(row, 6).Value = Math.Round(z.TonnesPerHour, 1);
            row++;
        }

        var wsS = wb.Worksheets.Add("Strategie legatura");
        row = 1;
        foreach (var rep in r.StrategyReports)
            foreach (var line in rep.Split('\n'))
                wsS.Cell(row++, 1).Value = line.TrimEnd('\r');

        var we = wb.Worksheets.Add("Eventi");
        string[] eh = { "N", "Tempo s", "Tipo", "Gravità", "Componente", "Pacco", "Messaggio" };
        for (int i = 0; i < eh.Length; i++) we.Cell(1, i + 1).Value = eh[i];
        row = 2;
        foreach (var e in engine.Events.Where(e => e.Severity >= EventSeverity.Info))
        {
            we.Cell(row, 1).Value = (double)e.Sequence;
            we.Cell(row, 2).Value = Math.Round(e.TimeS, 3);
            we.Cell(row, 3).Value = e.Type.ToString();
            we.Cell(row, 4).Value = e.Severity.ToString();
            we.Cell(row, 5).Value = e.ComponentId ?? "";
            we.Cell(row, 6).Value = e.PackageId ?? "";
            we.Cell(row, 7).Value = e.Message;
            row++;
        }

        var wc = wb.Worksheets.Add("Ciclogramma");
        string[] ch = { "Utenza", "Gruppo", "Inizio s", "Fine s", "Durata s", "Stato", "Dettaglio" };
        for (int i = 0; i < ch.Length; i++) wc.Cell(1, i + 1).Value = ch[i];
        row = 2;
        foreach (var t in engine.Recorder.Tracks)
            foreach (var s in t.Segments)
            {
                wc.Cell(row, 1).Value = t.Label;
                wc.Cell(row, 2).Value = t.Group.ToString();
                wc.Cell(row, 3).Value = Math.Round(s.StartS, 3);
                wc.Cell(row, 4).Value = Math.Round(s.EndS, 3);
                wc.Cell(row, 5).Value = Math.Round(s.DurationS, 3);
                wc.Cell(row, 6).Value = s.State.ToString();
                wc.Cell(row, 7).Value = s.Detail;
                row++;
            }

        foreach (var sheet in wb.Worksheets)
        {
            if (sheet.Name == "Riepilogo") continue;
            sheet.Row(1).Style.Font.Bold = true;
            sheet.Columns().AdjustToContents(1, 200);
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
