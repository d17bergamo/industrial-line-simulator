using LineSimulator.Core;
using LineSimulator.Models;

namespace LineSimulator.Tests;

/// <summary>Modifica multipla: scrive solo il parametro modificato.</summary>
public class MultiEditTests
{

    [Fact]
    public void MultiEditChangesOnlyTheEditedParameter()
    {
        var invs = new List<InverterConfig>
        {
            new() { Id = "A", HighSpeed = 0.6, Acceleration = 0.4, Deceleration = 0.5 },
            new() { Id = "B", HighSpeed = 0.6, Acceleration = 0.5, Deceleration = 0.6 },
            new() { Id = "C", HighSpeed = 0.6, Acceleration = 0.6, Deceleration = 0.7 }
        };
        var targets = invs.Cast<object>().ToList();
        var props = MultiEdit.Describe(typeof(InverterConfig));
        var high = props.Single(x => x.Name == nameof(InverterConfig.HighSpeed)).Property;
        var acc = props.Single(x => x.Name == nameof(InverterConfig.Acceleration)).Property;

        Assert.False(MultiEdit.Read(targets, high).Mixed);
        Assert.True(MultiEdit.Read(targets, acc).Mixed);

        Assert.Equal(3, MultiEdit.Apply(targets, high, 0.9));

        Assert.All(invs, i => Assert.Equal(0.9, i.HighSpeed));
        Assert.Equal(new[] { 0.4, 0.5, 0.6 }, invs.Select(i => i.Acceleration));
        Assert.Equal(new[] { 0.5, 0.6, 0.7 }, invs.Select(i => i.Deceleration));
        Assert.Equal(0, MultiEdit.Apply(targets, high, 0.9)); // nessun cambiamento: nessuna scrittura
    }

    [Fact]
    public void MultiEditNeverWritesReadOnlyProperties()
    {
        var targets = new List<object> { new InverterConfig { Id = "A" }, new InverterConfig { Id = "B" } };
        var ro = MultiEdit.Describe(typeof(InverterConfig)).Single(x => x.Name == nameof(InverterConfig.AccelerationTimeS));
        Assert.True(ro.IsReadOnly);
        Assert.Equal(0, MultiEdit.Apply(targets, ro.Property, 5.0));
    }

    [Fact]
    public void MultiEditRequiresSameTypeAndWorksOnPhotocells()
    {
        var mixed = new List<object> { new InverterConfig(), new PhotocellConfig() };
        Assert.False(MultiEdit.SameType(mixed, out _));

        var p = LegacySamples.CreateStandardLine();
        var idsBefore = p.Photocells.Select(x => x.Id).ToList();
        var pcs = p.Photocells.Cast<object>().ToList();
        Assert.True(MultiEdit.SameType(pcs, out var t));
        Assert.Equal(typeof(PhotocellConfig), t);
        var resp = MultiEdit.Describe(typeof(PhotocellConfig)).Single(x => x.Name == nameof(PhotocellConfig.ResponseTimeMs)).Property;
        MultiEdit.Apply(pcs, resp, 35.0);
        Assert.All(p.Photocells, x => Assert.Equal(35.0, x.ResponseTimeMs));
        Assert.Equal(idsBefore, p.Photocells.Select(x => x.Id).ToList()); // identificativi intatti
    }

    // ------------------------------------------------------------ interferenze e validazione
}
