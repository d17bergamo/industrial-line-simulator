using LineSimulator.Core;
using LineSimulator.Engine;
using LineSimulator.Engine.Runtime;
using LineSimulator.Infrastructure;
using LineSimulator.Models;
using Xunit;

namespace LineSimulator.Tests;

/// <summary>
/// Regole riviste con l'utente (2026-11-08): encoder con una regola unica, stringifascio che chiude solo con il pacco sotto,
/// momento unico dei controlli, avviso prima di modificare i progetti salvati.
/// </summary>
public class RuleReviewTests
{
    public static IEnumerable<object[]> Models() => SampleProjects.All.Select(m => new object[] { m.Name });
    private static LineProject Model(string name) => SampleProjects.All.Single(m => m.Name == name).Factory();

    // ------------------------------------------------------------------ encoder

    [Theory]
    [MemberData(nameof(Models))]
    public void Encoder_SamplesFollowTheSingleRule(string name)
    {
        var p = Model(name);
        Assert.Empty(EncoderRule.Problems(p));
        var first = EncoderRule.FirstStrapper(p)!;
        Assert.True(first.PositionMm - p.StrappingEncoder.PositionMm >= EncoderRule.MinToStrapperMm - 1e-6);
    }

    [Fact]
    public void Encoder_MovedByHand_GoesBackToTheRule_WithItsReferencePhotocell()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        double target = EncoderRule.TargetMm(p)!.Value;
        p.StrappingEncoder.PositionMm = target - 1500;
        Assert.NotEmpty(EncoderRule.Problems(p));
        Assert.NotEmpty(EncoderRule.Apply(p));
        Assert.Equal(target, p.StrappingEncoder.PositionMm, 6);
        Assert.All(p.Photocells.Where(x => x.Purpose == PhotocellPurpose.StrappingReference),
            pc => Assert.Equal(target - EncoderRule.ReferenceOffsetMm, pc.PositionMm, 6));
        Assert.Empty(EncoderRule.Problems(p));
        // le altre regole usano la stessa: niente da correggere né all'avvio né nella scheda Controllo
        Assert.DoesNotContain(EquipmentCheck.Run(p, fix: false), c => c.Code == "ENCODER_RULE");
        Assert.Empty(EncoderRule.Apply(p));
    }

    [Fact]
    public void Encoder_StrapperTooCloseToStart_ReferenceStaysAfterFC0_AndIsReported()
    {
        var p = SampleProjects.CreateOneStrapper12m();
        p.Strappers[0].PositionMm = 1000;
        EncoderRule.Apply(p);
        var refPc = p.Photocells.Single(x => x.Purpose == PhotocellPurpose.StrappingReference);
        Assert.True(refPc.PositionMm >= StrappingAreaRules.MinReferencePhotocellMm(p) - 1e-6);
        Assert.True(EncoderRule.StrapperTooCloseToStart(p));
        Assert.Contains(EncoderRule.Problems(p), m => m.Contains("troppo vicina"));
    }

    // ------------------------------------------------------------------ stringifascio

    [Fact]
    public void BundleClamps_OnlyTheOnesWithThePackageUnderneathClose()
    {
        var p = SampleProjects.All.Single(m => m.Name.StartsWith("3 legatrici")).Factory();
        Assert.True(p.BundleClamp.PerMachine);
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        Assert.True(e.CompletedNormally, e.FinishReason);
        // almeno una fermata con stringifascio rimasti aperti (pacco non sotto) e la legatura comunque fatta
        Assert.Contains(e.Events, x => x.Message.Contains("restano aperti"));
        Assert.Contains(e.Recorder.Frames, f => f.BundleClampClosed > 0.5 && f.BundleClampActive.Length > 1 && f.BundleClampActive.Any(a => !a));
        Assert.Contains(e.Recorder.Frames, f => f.BundleClampClosed > 0.5 && f.BundleClampActive.Any(a => a));
        Assert.DoesNotContain(e.Events, x => x.Message.Contains("non ha il pacco sotto"));
    }

    [Fact]
    public void BundleClamps_ClosedOnesAreAlwaysUnderThePackage()
    {
        var p = SampleProjects.All.Single(m => m.Name.StartsWith("3 legatrici")).Factory();
        var e = new SimulationEngine(p);
        e.RunToCompletion();
        var pos = LayoutRules.BundleClampPositions(e.Project).Select(x => x.PositionMm / 1000.0).ToArray();
        foreach (var f in e.Recorder.Frames.Where(f => f.BundleClampClosed > 0.99 && f.BundleClampActive.Length == pos.Length))
        {
            var pkg = f.Packages.Where(k => k.State == PackageState.Strapping).ToList();
            for (int i = 0; i < pos.Length; i++)
                if (f.BundleClampActive[i])
                    Assert.Contains(pkg, k => k.HeadM - k.LengthM <= pos[i] + 1e-6 && k.HeadM >= pos[i] - 1e-6);
        }
    }

    // ------------------------------------------------------------------ momento unico dei controlli

    [Theory]
    [MemberData(nameof(Models))]
    public void AutoFix_SamplesNeedNoCorrections(string name)
    {
        var r = ProjectAutoFix.Preview(Model(name));
        Assert.False(r.HasErrors, string.Join(" | ", r.Errors));
        Assert.True(!r.Changed, string.Join("\n", r.Messages));
    }

    [Fact]
    public void AutoFix_PreviewDoesNotTouchTheProject_RunAppliesTheSameList()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        p.StrappingEncoder.PositionMm -= 1200;
        string before = ProjectSerializer.Serialize(p);
        var preview = ProjectAutoFix.Preview(p);
        Assert.Equal(before, ProjectSerializer.Serialize(p));
        Assert.True(preview.Changed);
        var run = ProjectAutoFix.Run(p);
        Assert.Equal(preview.Messages, run.Messages);
        Assert.False(ProjectAutoFix.Preview(p).Changed);          // dopo l'applicazione non resta nulla da correggere
    }

    // ------------------------------------------------------------------ progetti salvati

    [Theory]
    [MemberData(nameof(Models))]
    public void Upgrade_CurrentProjectsOpenWithoutChanges(string name)
    {
        var r = ProjectUpgrade.Check(ProjectSerializer.Serialize(Model(name)));
        Assert.True(!r.HasChanges, string.Join("\n", r.RuleChanges.Concat(r.ValueChanges)));
        Assert.False(r.IsOlderSchema);
    }

    [Fact]
    public void Upgrade_ProjectThatBreaksTheRules_ListsTheChangesBeforeModifying()
    {
        var p = SampleProjects.CreateTwoStrappers12m();
        p.StrappingEncoder.PositionMm -= 1200;                    // salvato con la vecchia regola dell'encoder
        string json = ProjectSerializer.Serialize(p);
        var r = ProjectUpgrade.Check(json);
        Assert.True(r.HasChanges);
        Assert.Contains(r.RuleChanges, c => c.Contains(p.StrappingEncoder.Id));
        Assert.NotEmpty(r.ValueChanges);
        Assert.Empty(EncoderRule.Problems(r.Project));           // il progetto proposto è già corretto
    }

    [Fact]
    public void Upgrade_OlderSchemaIsRecognised()
    {
        var json = ProjectSerializer.Serialize(SampleProjects.CreateTwoStrappers12m()).Replace("\"schemaVersion\": 3", "\"schemaVersion\": 2");
        var r = ProjectUpgrade.Check(json);
        Assert.True(r.IsOlderSchema);
    }
}
