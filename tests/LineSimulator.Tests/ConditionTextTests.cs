using LineSimulator.Models;

namespace LineSimulator.Tests;

public class ConditionTextTests
{
    [Theory]
    [InlineData("PC(FC2) & !RUN(INV3)")]
    [InlineData("PC(FC1) | ZONE(INV2) & STR(L1)")]
    [InlineData("(PC(FC1) | SIG(A)) & !TRANSFER_HOME")]
    [InlineData("HEAD(1000,5000) & GAP(INV2,1500) | STOPPER")]
    [InlineData("TRUE")]
    public void FormatParse_RoundTrip(string text)
    {
        var c = ConditionText.Parse(text);
        string f = ConditionText.Format(c);
        Assert.Equal(f, ConditionText.Format(ConditionText.Parse(f)));
    }

    [Fact]
    public void Precedence_AndBindsTighterThanOr()
    {
        var c = Assert.IsType<OrCondition>(ConditionText.Parse("PC(A) | PC(B) & PC(C)"));
        Assert.IsType<PhotocellCondition>(c.Children[0]);
        Assert.IsType<AndCondition>(c.Children[1]);
    }

    [Fact]
    public void Negation_OfPhotocellBecomesFree()
    {
        var c = Assert.IsType<PhotocellCondition>(ConditionText.Parse("!PC(FC1)"));
        Assert.False(c.Occupied);
    }

    [Theory]
    [InlineData("PC(FC1) &")]
    [InlineData("FOO(1)")]
    [InlineData("(PC(FC1)")]
    [InlineData("HEAD(a,b)")]
    public void InvalidExpressions_Throw(string text)
    {
        Assert.Throws<FormatException>(() => ConditionText.Parse(text));
    }

    [Fact]
    public void ManualPlanText_RoundTrip()
    {
        var plans = ManualPlanText.Parse("P12: L3#1 ; L2#2 + L1#3");
        Assert.Single(plans);
        Assert.Equal(2, plans[0].Stops.Count);
        Assert.Equal(2, plans[0].Stops[1].Assignments.Count);
        Assert.Equal(2, plans[0].Stops[1].Assignments[1].StrapIndex);
        Assert.Equal("P12: L3#1 ; L2#2 + L1#3", ManualPlanText.Format(plans));
    }
}
