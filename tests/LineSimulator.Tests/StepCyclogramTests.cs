using System.Text;
using LineSimulator.Engine;
using LineSimulator.Infrastructure;
using Xunit;

namespace LineSimulator.Tests;

public class StepCyclogramTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.01, 0.5)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.51, 1.0)]
    [InlineData(1.01, 1.5)]
    [InlineData(2.0, 2.0)]
    [InlineData(7.3, 7.5)]
    public void RoundUp_GoesToNextHalfSecond(double t, double expected)
        => Assert.Equal(expected, StepCyclogram.RoundUp(t), 9);

    [Fact]
    public void Build_RoundsOnlyTheChartNotTheRecorder()
    {
        var rec = new SimulationRecorder();
        rec.Track("A", "Inverter A", TrackGroup.Inverter);
        rec.Set("A", 0.0, CycleState.Stopped);
        rec.Set("A", 1.23, CycleState.High);
        rec.Set("A", 4.01, CycleState.Stopped);
        rec.CloseAll(6.2);
        var rows = StepCyclogram.Build(rec);
        var r = Assert.Single(rows);
        Assert.Equal(new[] { 0.0, 1.5, 4.5 }, r.Points.Select(p => p.TimeS));
        Assert.Equal(6.5, r.EndS);
        // il registratore conserva i tempi esatti
        Assert.Equal(1.23, rec.Tracks[0].Segments[1].StartS, 9);
    }

    [Fact]
    public void Build_MergesSegmentsThatCollapseOnTheSameStep()
    {
        var rec = new SimulationRecorder();
        rec.Track("A", "A", TrackGroup.Inverter);
        rec.Set("A", 0.0, CycleState.Stopped);
        rec.Set("A", 1.1, CycleState.High);
        rec.Set("A", 1.3, CycleState.Stopped);   // 0,2 s: stesso gradino 1,5
        rec.CloseAll(3.0);
        var pts = StepCyclogram.Build(rec)[0].Points;
        Assert.Equal(StepLevel.Inactive, pts[^1].Level);
        Assert.True(pts.Zip(pts.Skip(1)).All(x => x.First.TimeS < x.Second.TimeS && x.First.Level != x.Second.Level));
    }

    [Fact]
    public void Pdf_IsWellFormed()
    {
        var rec = new SimulationRecorder();
        for (int i = 0; i < 60; i++)
        {
            rec.Track("T" + i, "Riga " + i + " (è)", TrackGroup.Inverter);
            rec.Set("T" + i, 0, CycleState.Stopped);
            rec.Set("T" + i, 2.2 + i * 0.1, CycleState.High);
        }
        rec.CloseAll(30);
        var bytes = StepCyclogramPdf.Build(StepCyclogram.Build(rec), "Prova");
        var s = Encoding.Latin1.GetString(bytes);
        Assert.StartsWith("%PDF-1.4", s);
        Assert.Contains("%%EOF", s);
        Assert.True(s.Split("/Type /Page ").Length - 1 >= 2);   // più pagine
        // offset xref coerenti
        int x = s.LastIndexOf("startxref", StringComparison.Ordinal);
        long off = long.Parse(s[(x + 10)..].Split('\n')[0]);
        Assert.StartsWith("xref", s[(int)off..]);
    }
}
