using LineSimulator.Core;
using LineSimulator.Models;
using Xunit;

namespace LineSimulator.Tests;

public class PhotocellNamingTests
{
    [Fact]
    public void Names_AreFcProgressiveUpstreamToDownstream_WithPurpose()
    {
        var p = new LineProject();
        p.Photocells.Add(new PhotocellConfig { Id = "B", Name = "FC9 accumulo", PositionMm = 5000 });
        p.Photocells.Add(new PhotocellConfig { Id = "A", Name = "Nuova fotocellula", PositionMm = 1000, Purpose = PhotocellPurpose.StrappingReference });
        p.Photocells.Add(new PhotocellConfig { Id = "C", Name = "uscita", PositionMm = 9000 });
        Assert.True(ProjectNormalizer.RenumberPhotocellNames(p));
        Assert.Equal("FC1 (riferimento encoder di legatura)", p.Photocells.Single(x => x.Id == "A").Name);
        Assert.Equal("FC2 (accumulo)", p.Photocells.Single(x => x.Id == "B").Name);
        Assert.Equal("FC3 (uscita)", p.Photocells.Single(x => x.Id == "C").Name);
        Assert.False(ProjectNormalizer.RenumberPhotocellNames(p));   // stabile
    }
}
