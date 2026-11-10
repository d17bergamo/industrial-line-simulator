using System.Globalization;
using System.Text;
using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Models;

namespace LineSimulator.Infrastructure;

/// <summary>
/// Ciclogramma da stampare in bianco e nero (A3 orizzontale, una pagina): il ciclo di un pacco a regime.
/// Una riga per ogni via a rulli con il profilo di velocità e una riga per macchina / fotocellula con le fasi a retini
/// (nero = lavora, righe oblique = preparazione, puntinato = movimento, tratteggio = attesa, barra sottile = fotocellula occupata),
/// asse del tempo con righe ogni secondo, eventi numerati, tempo ciclo a regime e le tabelle dei valori usati
/// (velocità, accelerazioni, spazi, tempi). Si simula una copia del progetto: il progetto aperto non cambia.
/// </summary>
public static class CyclogramPrintPdf
{
    private const double W = 1191, H = 842, M = 30, LabelW = 175;

    public static void Save(string path, LineProject project, double? lineSpeedMps = null) => File.WriteAllBytes(path, Build(project, lineSpeedMps));

    public static byte[] Build(LineProject source, double? lineSpeedMps = null)
    {
        var p = ProjectSerializer.Clone(source);
        try { ProjectAutoFix.Run(p); } catch { /* progetto incompleto: si simula com'è */ }
        p.Infeed.Continuous = false;
        p.Infeed.PackageCount = Math.Max(8, Math.Min(p.Infeed.PackageCount, 12));
        var e = new SimulationEngine(p);
        if (lineSpeedMps is double v) e.SetLineSpeed(v);
        e.RunToCompletion();
        var r = e.ComputeResults();
        return StepCyclogramPdf.Assemble(new List<string> { Page(e, r) }, W, H);
    }

    private enum Style { None, Work, Prep, Move, Wait, Occupied }

    private static Style StyleOf(TrackGroup g, CycleState s)
    {
        if (g == TrackGroup.Photocell) return s == CycleState.On ? Style.Occupied : Style.None;
        return s switch
        {
            CycleState.Working or CycleState.Closed or CycleState.Translating or CycleState.Depositing or CycleState.InPosition
                or CycleState.Aligned or CycleState.On or CycleState.Positioning or CycleState.Fault => Style.Work,
            CycleState.Preparing or CycleState.Aligning => Style.Prep,
            CycleState.Lifting or CycleState.Lowering or CycleState.Returning or CycleState.Closing or CycleState.Opening or CycleState.Unloading => Style.Move,
            CycleState.Waiting or CycleState.WaitConsent or CycleState.Consent or CycleState.Interlock => Style.Wait,
            _ => Style.None
        };
    }

    private static string Label(CycleState s) => s switch
    {
        CycleState.Working => "lavora", CycleState.Preparing => "ricarica", CycleState.Closed => "chiuso", CycleState.Closing => "chiude",
        CycleState.Opening => "apre", CycleState.Lifting => "solleva", CycleState.Translating => "trasla", CycleState.Lowering => "abbassa",
        CycleState.Depositing => "deposito", CycleState.Returning => "ritorno", CycleState.Waiting or CycleState.WaitConsent or CycleState.Consent => "attesa",
        CycleState.Interlock => "interblocco", CycleState.Aligned => "in battuta", CycleState.Aligning => "allinea", CycleState.Positioning => "posiziona",
        CycleState.Fault => "guasto", _ => ""
    };

    private static string Page(SimulationEngine e, SimulationResults r)
    {
        var p = e.Project;
        var c = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        // pacco di riferimento: a metà della finestra a regime (mai il primo, che trova la linea vuota)
        var exited = e.Packages.Where(k => k.ExitTimeS.HasValue).OrderBy(k => k.ExitTimeS).ToList();
        var cyc = new List<double>();
        for (int i = 1; i < exited.Count; i++) cyc.Add(exited[i].ExitTimeS!.Value - exited[i - 1].ExitTimeS!.Value);
        var (ws, we) = ResultsCalculator.SteadyWindow(cyc, Math.Max(1, p.Infeed.TypeSequence.Count));
        int pi = exited.Count == 0 ? -1 : Math.Clamp(Math.Max(2, (ws + we) / 2), 0, exited.Count - 1);
        var byEntry = e.Packages.OrderBy(k => k.EntryTimeS).ToList();
        var pk = pi >= 0 ? exited[pi] : byEntry.Skip(1).FirstOrDefault() ?? byEntry.FirstOrDefault();
        double tc0 = r.CycleTimeMeanS ?? 60;
        // fine finestra: il pacco arriva in battuta, il trasferitore lo preleva e torna (o al più due tempi ciclo)
        var stopperSeg = pk == null ? null : e.Recorder.Tracks.FirstOrDefault(x => x.Group == TrackGroup.Stopper)?.Segments
            .FirstOrDefault(sg => sg.StartS >= pk.EntryTimeS && StyleOf(TrackGroup.Stopper, sg.State) == Style.Work);
        var trfReturn = stopperSeg == null ? null : e.Recorder.Tracks.FirstOrDefault(x => x.Group == TrackGroup.Transfer)?.Segments
            .FirstOrDefault(sg => sg.StartS >= stopperSeg.StartS && sg.State == CycleState.Returning);
        double t0 = pk != null ? Math.Max(0, Math.Floor(pk.EntryTimeS - 2)) : 0;
        double t1 = trfReturn != null ? Math.Ceiling(trfReturn.EndS + 3) : stopperSeg != null ? stopperSeg.StartS + 30 : t0 + 1.3 * tc0;
        t1 = Math.Min(t1, t0 + Math.Max(2.2 * tc0, 60));
        t1 = Math.Min(Math.Max(t1, t0 + 20), Math.Max(e.Time, t0 + 20));
        double span = t1 - t0;

        // ---------------------------------------------------- intestazione
        string title = string.IsNullOrWhiteSpace(p.Name) ? "Linea di legatura" : p.Name;
        Text(c, M, H - M - 8, 16, "Ciclogramma di un ciclo a regime");
        string banking = p.Simulation.Banking == AccumulationMode.DoubleBanking ? "double banking" : "single banking";
        string heading = p.Simulation.Heading == StopReference.Tail ? "intestatura di coda" : "intestatura di testa";
        Text(c, M, H - M - 24, 9.5, $"{title} · pacco {pk?.Type.Id ?? "-"} · {banking}, {heading} · velocità linea {N(e.LineSpeedMps ?? e.Zones.Min(z => z.Config.HighSpeed))} m/s");
        string prod = r.PackagesPerHour is double pph ? $"Produttività a regime: {N(pph, "0.0")} pacchi/h · {N(r.TonnesPerHour ?? 0, "0.0")} t/h" : "Produttività: non misurata";
        Text(c, W - M - 330, H - M - 8, 10, prod);
        Text(c, W - M - 330, H - M - 22, 8.5, "Scala: una riga ogni secondo, righe spesse ogni 10 s");
        Text(c, W - M - 330, H - M - 34, 8.5, "Data: ____________   Progetto / cliente: ______________________");
        c.Append("1.5 w\n"); Line(c, M, H - M - 40, W - M, H - M - 40); c.Append("0.5 w\n");

        // ---------------------------------------------------- righe
        var rows = new List<(string Label, string Sub, Action<double, double> Draw)>();
        double px0 = M + LabelW, px1 = W - M;
        double X(double t) => px0 + (px1 - px0) * (t - t0) / span;

        var zones = e.Zones.ToList();
        for (int zi = 0; zi < zones.Count; zi++)
        {
            var z = zones[zi];
            int idx = zi;
            double vmax = Math.Max(0.1, z.Config.HighSpeed);
            string secs = string.Join(" + ", p.Sections.Where(s => s.EffectiveZoneId == z.Id).OrderBy(s => s.StartMm).Select(s => s.Id));
            double len = p.Sections.Where(s => s.EffectiveZoneId == z.Id).Sum(s => s.LengthMm);
            rows.Add(($"Via a rulli {secs}", $"{N(len, "0")} mm · inverter {z.Id}", (y, h) =>
            {
                var pts = new StringBuilder();
                bool first = true;
                for (double t = t0; t <= t1 + 1e-9; t += 0.25)
                {
                    var f = e.Recorder.FrameAt(t);
                    double sp = f != null && idx < f.ZoneSpeeds.Length ? Math.Abs(f.ZoneSpeeds[idx]) : 0;
                    double yy = y + 3 + (h - 8) * Math.Min(1.2, sp / vmax) / 1.2;
                    pts.Append(F(X(t))).Append(' ').Append(F(yy)).Append(first ? " m " : " l ");
                    first = false;
                }
                c.Append("1.6 w\n").Append(pts).Append("S\n0.5 w\n");
                Text(c, px0 - 22, y + 3 + (h - 8) / 1.2 - 3, 6.5, N(vmax));
            }));
        }
        var groups = new[] { TrackGroup.Photocell, TrackGroup.Strapper, TrackGroup.Clamp, TrackGroup.Stop, TrackGroup.Stopper, TrackGroup.Transfer, TrackGroup.Scale, TrackGroup.Rejector };
        foreach (var g in groups)
            foreach (var tr in e.Recorder.Tracks.Where(x => x.Group == g))
            {
                var segs = tr.Segments.Where(s => s.EndS > t0 && s.StartS < t1 && StyleOf(g, s.State) != Style.None).ToList();
                if (segs.Count == 0 && g != TrackGroup.Strapper) continue;
                string sub = SubLabel(p, tr.Key);
                rows.Add((tr.Label, sub, (y, h) =>
                {
                    foreach (var s in segs)
                    {
                        double a = X(Math.Max(t0, s.StartS)), b = X(Math.Min(t1, s.EndS));
                        if (b - a < 0.3) continue;
                        Bar(c, a, y + 3, b - a, h - 6, StyleOf(g, s.State));
                        double dur = Math.Min(t1, s.EndS) - Math.Max(t0, s.StartS);
                        string l = StyleOf(g, s.State) == Style.Occupied ? "" : $"{Label(s.State)} {N(dur, "0.0")}";
                        if (l.Length > 0 && b - a > l.Length * 3.6 + 4)
                        {
                            bool dark = StyleOf(g, s.State) == Style.Work;
                            if (!dark) { Gray(c, 1); Rect(c, (a + b) / 2 - l.Length * 1.8 - 1, y + h / 2 - 4, l.Length * 3.6 + 2, 8, true); }
                            Gray(c, dark ? 1 : 0);
                            Text(c, (a + b) / 2 - l.Length * 1.75, y + h / 2 - 2.5, 6.5, l);
                            Gray(c, 0);
                        }
                    }
                }));
            }

        double tablesH = 215, legendH = 22, chartTop = H - M - 62, chartBottom = M + tablesH + legendH + 64;
        double rowH = Math.Min(34, (chartTop - chartBottom) / Math.Max(1, rows.Count));
        double gy1 = chartTop - rows.Count * rowH;

        // griglia del tempo
        for (int ts = (int)Math.Ceiling(t0); ts <= t1; ts++)
        {
            bool major = (ts - (int)Math.Ceiling(t0)) % 10 == 0;
            Gray(c, major ? 0 : 0.78);
            c.Append(major ? "0.6 w\n" : "0.25 w\n");
            Line(c, X(ts), gy1, X(ts), chartTop);
            if (major) { Gray(c, 0); Text(c, X(ts) - 6, chartTop + 4, 7.5, $"{ts - t0:0} s"); Text(c, X(ts) - 4, gy1 - 10, 7.5, $"{ts - t0:0}"); }
        }
        Gray(c, 0); c.Append("0.5 w\n");
        for (int i = 0; i < rows.Count; i++)
        {
            double y = chartTop - (i + 1) * rowH;
            Line(c, M, y + rowH, px1, y + rowH);
            Text(c, M, y + rowH - 10, 8, Trim(rows[i].Label, 34));
            Text(c, M, y + rowH - 19, 6.5, Trim(rows[i].Sub, 44));
            rows[i].Draw(y, rowH);
        }
        Line(c, M, gy1, px1, gy1);

        // eventi del pacco di riferimento
        var events = new List<(double T, string Text)>();
        if (pk != null)
        {
            events.Add((pk.EntryTimeS, $"Il pacco {pk.Type.Id} entra in linea (prima fotocellula: identificazione)."));
            var strap = e.Recorder.Tracks.Where(x => x.Group == TrackGroup.Strapper).SelectMany(x => x.Segments)
                .Where(s => s.State == CycleState.Working && s.StartS >= pk.EntryTimeS).OrderBy(s => s.StartS).ToList();
            if (strap.Count > 0)
            {
                events.Add((strap[0].StartS, "Prima legatura: il pacco è fermo in posizione, stringifascio chiuso."));
                // il pacco lascia la via di legatura quando impegna la fotocellula di uscita legatura
                var exitPc = p.Photocells.FirstOrDefault(x => x.Purpose == PhotocellPurpose.StrappingExit);
                double leave = (exitPc == null ? null : e.Recorder.Tracks.FirstOrDefault(x => x.Key == "PC:" + exitPc.Id)?.Segments
                    .FirstOrDefault(sg => sg.State == CycleState.On && sg.StartS > strap[0].StartS)?.StartS) ?? (stopperSeg?.StartS ?? double.MaxValue);
                double lastEnd = strap.Where(s => s.StartS < leave).Select(s => s.EndS).DefaultIfEmpty(strap[0].EndS).Max();
                events.Add((lastEnd, "Ultima legatura finita: il pacco lascia la via di legatura."));
            }
            if (stopperSeg != null) events.Add((stopperSeg.StartS, "Il pacco arriva in battuta sul fermo finale."));
            var lift = e.Recorder.Tracks.FirstOrDefault(x => x.Group == TrackGroup.Transfer)?.Segments
                .FirstOrDefault(s => s.StartS >= pk.EntryTimeS && s.State == CycleState.Lifting);
            if (lift != null) events.Add((lift.StartS, "Il trasferitore solleva e porta via il pacco."));
            if (trfReturn != null) events.Add((trfReturn.StartS, "Pacco depositato: il trasferitore torna, pronto per il successivo."));
        }
        events = events.Where(x => x.T >= t0 && x.T <= t1).OrderBy(x => x.T).ToList();
        double prevX = double.NegativeInfinity, prevDy = 0;
        for (int i = 0; i < events.Count; i++)
        {
            double xx = X(events[i].T);
            double dy = xx - prevX < 16 ? prevDy + 15 : 0;                 // eventi quasi contemporanei: cerchi uno sotto l'altro
            c.Append("[4 3] 0 d 0.9 w\n"); Line(c, xx, gy1, xx, chartTop); c.Append("[] 0 d 0.5 w\n");
            Circle(c, xx, gy1 - 22 - dy, 6.5);
            Text(c, xx - 2.5, gy1 - 25 - dy, 8, (i + 1).ToString(ci));
            prevX = xx; prevDy = dy;
        }
        // tempo ciclo a regime
        if (r.CycleTimeMeanS is double tc && pk != null)
        {
            double a = X(pk.EntryTimeS), b = X(Math.Min(t1, pk.EntryTimeS + tc)), yb = gy1 - 52;
            c.Append("1.2 w\n"); Line(c, a, yb, b, yb); Line(c, a, yb - 4, a, yb + 4); Line(c, b, yb - 4, b, yb + 4); c.Append("0.5 w\n");
            string tt = $"Tempo ciclo a regime Tc = {N(tc, "0.0")} s";
            Gray(c, 1); Rect(c, (a + b) / 2 - tt.Length * 2.3, yb - 6, tt.Length * 4.6, 12, true); Gray(c, 0);
            Text(c, (a + b) / 2 - tt.Length * 2.2, yb - 3, 8.5, tt);
        }

        // legenda
        double ly = M + tablesH + 8;
        double lx = M;
        void Leg(Style s, string t) { Bar(c, lx, ly, 22, 9, s); Text(c, lx + 27, ly + 1.5, 7.5, t); lx += 32 + t.Length * 4.1; }
        Leg(Style.Work, "lavora / in moto");
        Leg(Style.Prep, "preparazione (ricarica filo)");
        Leg(Style.Move, "movimento (apre, chiude, solleva, ritorna)");
        Leg(Style.Wait, "attesa di un consenso");
        Leg(Style.Occupied, "fotocellula occupata");
        c.Append("1.6 w\n"); Line(c, lx, ly + 1, lx + 6, ly + 8); Line(c, lx + 6, ly + 8, lx + 18, ly + 8); Line(c, lx + 18, ly + 8, lx + 24, ly + 1); c.Append("0.5 w\n");
        Text(c, lx + 29, ly + 1.5, 7.5, "velocità della via (m/s)");

        // ---------------------------------------------------- tabelle
        double ty = M + tablesH - 12;
        double colW = (W - 2 * M - 20) / 2;
        Text(c, M, ty, 9, "EVENTI");
        double ey = ty - 12;
        for (int i = 0; i < events.Count; i++) { Text(c, M, ey, 7.5, $"{i + 1}.  t = {N(events[i].T - t0, "0.0")} s  {events[i].Text}"); ey -= 10; }
        ey -= 6;
        Text(c, M, ey, 9, "VIE A RULLI: VELOCITÀ, ACCELERAZIONI, SPAZI");
        ey -= 4;
        var secRows = p.Sections.OrderBy(s => s.StartMm).Select(s => new[]
        {
            s.Id, N(s.LengthMm, "0") + " mm", $"{N(s.MaxSpeed)} / {N(s.HighSpeed)} / {N(s.LowSpeed)} m/s", $"{N(s.Acceleration)} / {N(s.Deceleration)} m/s²",
            N(s.Acceleration > 0 ? s.HighSpeed / s.Acceleration : 0, "0.0") + " s", N(s.Deceleration > 0 ? s.HighSpeed * s.HighSpeed / (2 * s.Deceleration) * 1000 : 0, "0") + " mm"
        }).ToList();
        ey = Table(c, M, ey, colW, new[] { "Modulo", "Lunghezza", "V max / nom. / min", "Acc. / dec.", "Rampa 0->Vnom", "Arresto da Vnom" }, secRows,
            new[] { 0.12, 0.15, 0.25, 0.18, 0.14, 0.16 });

        double rx = M + colW + 20, ry = ty;
        Text(c, rx, ry, 9, "MACCHINE: TEMPI, VELOCITÀ, CORSE");
        ry -= 4;
        var mach = new List<string[]>();
        foreach (var s in p.Strappers.OrderBy(x => x.PositionMm))
            mach.Add(new[] { s.Name, N(s.PositionMm, "0") + " mm", $"{(s.MachineType == StrapperMachineType.SMA ? "riarmo" : "ricarica filo")} {N(s.ArmingTimeS)} s · ciclo {N(s.StrappingTimeS)} s", s.IsEnabled ? "abilitata" : "disabilitata" });
        if (p.BundleClamp.IsEnabled)
            mach.Add(new[] { "Stringifascio", p.BundleClamp.PerMachine ? $"{N(p.BundleClamp.MachineOffsetMm, "0")} mm prima di ogni legatrice" : N(p.BundleClamp.PositionMm, "0") + " mm",
                $"chiude {N(p.BundleClamp.CloseTimeS)} s · tiene {N(p.BundleClamp.HoldTimeS)} s · apre {N(p.BundleClamp.OpenTimeS)} s", "" });
        var trf = p.Transfer;
        mach.Add(new[] { "Trasferitore a catene", N(trf.PositionMm, "0") + " mm, " + N(trf.LengthMm, "0") + " mm",
            $"solleva {N(trf.LiftTimeS)} s · abbassa {N(trf.LowerTimeS)} s · deposito {N(trf.DepositTimeS)} s",
            $"corsa {N(trf.LiftStrokeMm, "0")} mm · traslazione {N(trf.TranslationStrokeMm, "0")} mm a {N(trf.TranslationSpeed)} m/s" });
        foreach (var sc in p.Scales.Where(x => x.IsEnabled))
            mach.Add(new[] { sc.Name, sc.Mounting == ScaleMounting.InLine ? "integrato nella via" : "bancale", $"pesata {N(sc.AcquisitionTimeS)} s", "" });
        ry = Table(c, rx, ry, colW, new[] { "Macchina", "Posizione", "Tempi", "Velocità / corse" }, mach, new[] { 0.2, 0.2, 0.33, 0.27 });
        ry -= 8;
        Text(c, rx, ry, 9, "FOTOCELLULE");
        ry -= 4;
        double rule = TransferEntryRule.RequiredDistanceMm(p);
        var pcs = p.Photocells.Where(x => x.IsEnabled).OrderBy(x => x.PositionMm).Select(x => new[]
        {
            x.Id, N(x.PositionMm, "0") + " mm", x.Name,
            x.ActsAsTransferStop ? $"Vmax²/(2a) + 0,2·Vmax -> {N(rule, "0")} mm dall'ingombro più a monte" : x.Action == PhotocellAction.SlowDown ? "rallentamento" : ""
        }).ToList();
        Table(c, rx, ry, colW, new[] { "Id", "Posizione", "Nome", "Regola" }, pcs, new[] { 0.1, 0.15, 0.3, 0.45 });
        return c.ToString();
    }

    private static string SubLabel(LineProject p, string key)
    {
        string id = key.Contains(':') ? key[(key.IndexOf(':') + 1)..] : key;
        var pc = p.Photocells.FirstOrDefault(x => x.Id == id);
        if (pc != null) return $"{N(pc.PositionMm, "0")} mm";
        var st = p.Strappers.FirstOrDefault(x => x.Id == id);
        if (st != null) return $"{N(st.PositionMm, "0")} mm · {(st.MachineType == StrapperMachineType.SMA ? "reggettatrice" : "legatrice a filo")}";
        return key switch { "STOPPER" => "fine dell'ultimo modulo", "KNOF" => "stringifascio", "CMP" => "compattatore", _ => "" };
    }

    // ---------------------------------------------------- disegno

    private static void Bar(StringBuilder c, double x, double y, double w, double h, Style s)
    {
        switch (s)
        {
            case Style.Work: Gray(c, 0); Rect(c, x, y, w, h, true); break;
            case Style.Occupied: Gray(c, 0); Rect(c, x, y + h * 0.35, w, h * 0.3, true); break;
            case Style.Prep:
                c.Append("q ").Append($"{F(x)} {F(y)} {F(w)} {F(h)} re W n\n").Append("0.6 w\n");
                for (double d = -h; d < w; d += 4) Line(c, x + d, y, x + d + h, y + h);
                c.Append("Q\n"); Gray(c, 0); Rect(c, x, y, w, h, false); break;
            case Style.Move:
                for (double yy = y + 1.5; yy < y + h - 0.5; yy += 3)
                    for (double xx = x + 1.5; xx < x + w - 0.5; xx += 3) Rect(c, xx - 0.45, yy - 0.45, 0.9, 0.9, true);
                Rect(c, x, y, w, h, false); break;
            case Style.Wait:
                c.Append("[3 2] 0 d\n"); Rect(c, x, y, w, h, false); c.Append("[] 0 d\n"); break;
        }
    }

    private static double Table(StringBuilder c, double x, double y, double w, string[] head, List<string[]> rows, double[] cols)
    {
        double rh = 10.5;
        double cx = x;
        y -= rh;
        for (int i = 0; i < head.Length; i++) { Text(c, cx + 2, y + 3, 7.2, head[i]); cx += cols[i] * w; }
        c.Append("1 w\n"); Line(c, x, y, x + w, y); c.Append("0.4 w\n");
        foreach (var r in rows)
        {
            y -= rh;
            cx = x;
            for (int i = 0; i < head.Length && i < r.Length; i++)
            {
                int max = (int)(cols[i] * w / 3.6);
                Text(c, cx + 2, y + 3, 7, Trim(r[i], max));
                cx += cols[i] * w;
            }
            Line(c, x, y, x + w, y);
        }
        return y - 6;
    }

    private static void Circle(StringBuilder c, double cx, double cy, double r)
    {
        const double k = 0.5523;
        c.Append($"1 g {F(cx + r)} {F(cy)} m {F(cx + r)} {F(cy + k * r)} {F(cx + k * r)} {F(cy + r)} {F(cx)} {F(cy + r)} c ")
         .Append($"{F(cx - k * r)} {F(cy + r)} {F(cx - r)} {F(cy + k * r)} {F(cx - r)} {F(cy)} c ")
         .Append($"{F(cx - r)} {F(cy - k * r)} {F(cx - k * r)} {F(cy - r)} {F(cx)} {F(cy - r)} c ")
         .Append($"{F(cx + k * r)} {F(cy - r)} {F(cx + r)} {F(cy - k * r)} {F(cx + r)} {F(cy)} c b 0 g\n");
    }

    private static string F(double v) => StepCyclogramPdf.F(v);
    private static void Text(StringBuilder c, double x, double y, double size, string s) => StepCyclogramPdf.Text(c, x, y, size, s);
    private static void Line(StringBuilder c, double x1, double y1, double x2, double y2) => StepCyclogramPdf.Line(c, x1, y1, x2, y2);
    private static void Gray(StringBuilder c, double g) => c.Append($"{F(g)} g {F(g)} G\n");
    private static void Rect(StringBuilder c, double x, double y, double w, double h, bool fill) => c.Append($"{F(x)} {F(y)} {F(w)} {F(h)} re {(fill ? "f" : "S")}\n");
    private static string Trim(string s, int n) => n <= 2 || s.Length <= n ? s : s[..(n - 1)] + ".";
    private static string N(double v, string fmt = "0.0#") => v.ToString(fmt, CultureInfo.InvariantCulture).Replace('.', ',');
}
