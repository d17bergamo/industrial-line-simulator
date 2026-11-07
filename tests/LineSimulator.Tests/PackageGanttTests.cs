using System.Text;
using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Infrastructure;
using LineSimulator.Models;
using Xunit;

namespace LineSimulator.Tests;

/// <summary>Gantt dell'avanzamento dei pacchi: tratti di movimento e sosta con distanza, velocità e accelerazione.</summary>
public class PackageGanttTests
{
    private static (LineProject P, SimulationEngine E, List<GanttRow> Rows) Run()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        return (p, e, PackageGantt.Build(e.Recorder));
    }

    [Fact]
    public void Gantt_HasOneRowPerPackage_WithStrappingAndTransport()
    {
        var (_, e, rows) = Run();
        Assert.Equal(e.Packages.Count, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.NotEmpty(r.Segments);
            Assert.Contains(r.Segments, s => s.State == PackageState.Strapping);
            Assert.Contains(r.Segments, s => s.Moving && s.DistanceM > 0.5);
            // tratti consecutivi e ordinati nel tempo
            Assert.True(r.Segments.Zip(r.Segments.Skip(1)).All(x => x.Second.StartS >= x.First.StartS - 1e-9 && x.First.EndS <= x.Second.StartS + 0.051));
        });
    }

    [Fact]
    public void Gantt_SpeedAndAcceleration_ArePlausible()
    {
        var (p, _, rows) = Run();
        double vMax = p.Sections.Max(s => Math.Max(s.HighSpeed, s.MaxSpeed));
        foreach (var r in rows)
            foreach (var s in r.Segments.Where(s => s.Moving))
            {
                Assert.True(s.MaxSpeed <= vMax + 0.01, $"{r.PackageId} {s.Label}: {s.MaxSpeed} > {vMax}");
                Assert.True(s.MeanSpeed <= s.MaxSpeed + 1e-6);
                Assert.True(s.MaxAcceleration >= 0 && s.MaxDeceleration >= 0);
            }
        Assert.Contains(rows.SelectMany(r => r.Segments), s => s.MaxAcceleration > 0.05);
    }

    [Fact]
    public void Gantt_DistancesMatchHeadTravelOnRollers()
    {
        var (_, _, rows) = Run();
        foreach (var r in rows)
        {
            var onRollers = r.Segments.Where(s => s.State != PackageState.OnTransfer).ToList();
            double travel = onRollers[^1].ToM - onRollers[0].FromM;
            double moved = onRollers.Where(s => s.Moving).Sum(s => s.ToM - s.FromM);
            Assert.True(Math.Abs(travel - moved) < 0.05, $"{r.PackageId}: {travel:0.000} vs {moved:0.000}");
        }
    }

    [Fact]
    public void Pdf_ContainsGanttDetailAndSteps()
    {
        var (_, e, rows) = Run();
        var bytes = PackageGanttPdf.Build(rows, StepCyclogram.Build(e.Recorder), "Prova (è)");
        var text = Encoding.Latin1.GetString(bytes);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Contains("Gantt dell'avanzamento dei pacchi", text);
        Assert.Contains("tratti dei pacchi", text);
        Assert.Contains("m/s", text);
        Assert.Contains(" rg\n", text);
        Assert.True(System.Text.RegularExpressions.Regex.Matches(text, "/Type /Page /Parent").Count >= 3);
    }

    [Fact]
    public void Pdf_WithoutPackages_IsStillValid()
    {
        var bytes = PackageGanttPdf.Build(new List<GanttRow>(), null, "Vuoto");
        Assert.StartsWith("%PDF", Encoding.Latin1.GetString(bytes));
    }
}
