using System.Globalization;
using System.Text;

namespace LineSimulator.Models;

/// <summary>
/// Rappresentazione testuale delle condizioni logiche, modificabile dal pannello proprietà.
///
/// Sintassi:  espr := termine ('|' termine)* ;  termine := fattore ('&amp;' fattore)* ;
///            fattore := '!' fattore | '(' espr ')' | atomo
/// Atomi:  TRUE, FALSE, PC(id), RUN(inverter), ZONE(inverter), STR(legatrice), STOPPER,
///         TRANSFER_HOME, HEAD(daMm,aMm), GAP(inverter,mm), SIG(nome)
/// Esempio:  PC(FC2) &amp; !RUN(INV3) | SIG(Consenso)
/// </summary>
public static class ConditionText
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static string Format(LogicCondition? c) => c switch
    {
        null => "FALSE",
        ConstantCondition k => k.Value ? "TRUE" : "FALSE",
        AndCondition a => a.Children.Count == 0 ? "FALSE" : string.Join(" & ", a.Children.Select(ch => Wrap(ch, true))),
        OrCondition o => o.Children.Count == 0 ? "FALSE" : string.Join(" | ", o.Children.Select(ch => Wrap(ch, false))),
        NotCondition n => "!" + Wrap(n.Child, true, true),
        PhotocellCondition p => p.Occupied ? $"PC({p.PhotocellId})" : $"!PC({p.PhotocellId})",
        InverterRunningCondition r => r.Running ? $"RUN({r.InverterId})" : $"!RUN({r.InverterId})",
        ZoneOccupiedCondition z => $"ZONE({z.InverterId})",
        StrapperBusyCondition s => $"STR({s.StrapperId})",
        StopperOccupiedCondition => "STOPPER",
        TransferAtHomeCondition => "TRANSFER_HOME",
        PackageHeadInRangeCondition h => $"HEAD({h.FromMm.ToString(Ci)},{h.ToMm.ToString(Ci)})",
        GapAheadLessThanCondition g => $"GAP({g.InverterId},{g.GapMm.ToString(Ci)})",
        SignalCondition s => $"SIG({s.SignalName})",
        _ => c.Describe()
    };

    private static string Wrap(LogicCondition c, bool insideAnd, bool unary = false)
    {
        string s = Format(c);
        bool needs = c is OrCondition || (unary && c is AndCondition) || (!insideAnd && false);
        return needs ? "(" + s + ")" : s;
    }

    public static LogicCondition Parse(string text)
    {
        var p = new Parser(text ?? string.Empty);
        var c = p.ParseExpr();
        p.SkipWs();
        if (!p.End) throw new FormatException($"Carattere inatteso '{p.Peek}' in posizione {p.Pos + 1}.");
        return c;
    }

    private sealed class Parser
    {
        private readonly string _s;
        public int Pos;
        public Parser(string s) { _s = s; }
        public bool End => Pos >= _s.Length;
        public char Peek => End ? '\0' : _s[Pos];
        public void SkipWs() { while (!End && char.IsWhiteSpace(_s[Pos])) Pos++; }

        public LogicCondition ParseExpr()
        {
            var items = new List<LogicCondition> { ParseTerm() };
            SkipWs();
            while (!End && Peek == '|') { Pos++; items.Add(ParseTerm()); SkipWs(); }
            return items.Count == 1 ? items[0] : new OrCondition { Children = items };
        }

        private LogicCondition ParseTerm()
        {
            var items = new List<LogicCondition> { ParseFactor() };
            SkipWs();
            while (!End && Peek == '&') { Pos++; items.Add(ParseFactor()); SkipWs(); }
            return items.Count == 1 ? items[0] : new AndCondition { Children = items };
        }

        private LogicCondition ParseFactor()
        {
            SkipWs();
            if (End) throw new FormatException("Espressione incompleta.");
            if (Peek == '!')
            {
                Pos++;
                var inner = ParseFactor();
                return inner switch
                {
                    PhotocellCondition pc => new PhotocellCondition { PhotocellId = pc.PhotocellId, Occupied = !pc.Occupied },
                    InverterRunningCondition r => new InverterRunningCondition { InverterId = r.InverterId, Running = !r.Running },
                    _ => new NotCondition { Child = inner }
                };
            }
            if (Peek == '(')
            {
                Pos++;
                var e = ParseExpr();
                SkipWs();
                if (Peek != ')') throw new FormatException($"Manca ')' in posizione {Pos + 1}.");
                Pos++;
                return e;
            }
            int start = Pos;
            while (!End && (char.IsLetterOrDigit(Peek) || Peek == '_')) Pos++;
            string name = _s.Substring(start, Pos - start).ToUpperInvariant();
            if (name.Length == 0) throw new FormatException($"Atteso un termine in posizione {Pos + 1}.");
            var args = new List<string>();
            SkipWs();
            if (Peek == '(')
            {
                Pos++;
                var sb = new StringBuilder();
                while (!End && Peek != ')') { sb.Append(Peek); Pos++; }
                if (End) throw new FormatException("Manca ')' negli argomenti.");
                Pos++;
                args = sb.ToString().Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            }
            string A(int i) => i < args.Count ? args[i] : throw new FormatException($"{name}: argomento {i + 1} mancante.");
            double D(int i) => double.TryParse(A(i), NumberStyles.Float, Ci, out var v) ? v : throw new FormatException($"{name}: '{A(i)}' non è un numero.");
            return name switch
            {
                "TRUE" => new ConstantCondition { Value = true },
                "FALSE" => new ConstantCondition { Value = false },
                "PC" => new PhotocellCondition { PhotocellId = A(0), Occupied = true },
                "RUN" => new InverterRunningCondition { InverterId = A(0), Running = true },
                "ZONE" => new ZoneOccupiedCondition { InverterId = A(0) },
                "STR" => new StrapperBusyCondition { StrapperId = A(0) },
                "STOPPER" => new StopperOccupiedCondition(),
                "TRANSFER_HOME" => new TransferAtHomeCondition(),
                "HEAD" => new PackageHeadInRangeCondition { FromMm = D(0), ToMm = D(1) },
                "GAP" => new GapAheadLessThanCondition { InverterId = A(0), GapMm = D(1) },
                "SIG" => new SignalCondition { SignalName = A(0) },
                _ => throw new FormatException($"Termine sconosciuto '{name}'.")
            };
        }
    }
}

/// <summary>Formato testuale dei piani di legatura manuali: "P12: L3#1 ; L2#2 + L1#3 | P8: L1#1 ; L1#2".</summary>
public static class ManualPlanText
{
    public static string Format(IEnumerable<ManualStrappingPlan> plans) =>
        string.Join(" | ", plans.Select(p => p.PackageTypeId + ": " +
            string.Join(" ; ", p.Stops.Select(s => string.Join(" + ", s.Assignments.Select(a => $"{a.StrapperId}#{a.StrapIndex + 1}"))))));

    public static List<ManualStrappingPlan> Parse(string text)
    {
        var result = new List<ManualStrappingPlan>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var block in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = block.IndexOf(':');
            if (colon <= 0) throw new FormatException($"Manca 'TIPO:' in '{block}'.");
            var plan = new ManualStrappingPlan { PackageTypeId = block[..colon].Trim() };
            foreach (var stopTxt in block[(colon + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var stop = new ManualStrappingStop();
                foreach (var a in stopTxt.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = a.Split('#');
                    if (parts.Length != 2 || !int.TryParse(parts[1].Trim(), out int idx) || idx < 1)
                        throw new FormatException($"Assegnazione non valida '{a}': usare LEGATRICE#numeroLegatura (es. L1#2).");
                    stop.Assignments.Add(new StrapAssignment { StrapperId = parts[0].Trim(), StrapIndex = idx - 1 });
                }
                plan.Stops.Add(stop);
            }
            result.Add(plan);
        }
        return result;
    }
}
