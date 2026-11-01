using LineSimulator.Core;

namespace LineSimulator.Tests;

public class KinematicsTests
{
    [Fact]
    public void BrakingDistance_IsVSquaredOverTwoD()
    {
        Assert.Equal(0.3, Kinematics.BrakingDistance(0.6, 0.6), 9);
        Assert.Equal(0, Kinematics.BrakingDistance(0, 0.6), 9);
    }

    [Fact]
    public void StoppingDistance_IncludesResponseTime()
    {
        Assert.Equal(0.6 * 0.02 + 0.3, Kinematics.StoppingDistance(0.6, 0.02, 0.6), 9);
    }

    [Fact]
    public void PointToPoint_Trapezoidal()
    {
        // 15 m, vmax 0.6, a 0.5, d 0.6: 1.2 + 1.0 + (15 - 0.36 - 0.3) / 0.6
        double expected = 1.2 + 1.0 + (15 - 0.36 - 0.3) / 0.6;
        Assert.Equal(expected, Kinematics.PointToPointTime(15, 0.6, 0.5, 0.6), 9);
    }

    [Fact]
    public void PointToPoint_Triangular()
    {
        // 0.2 m non basta a raggiungere 0.6 m/s: vPeak = sqrt(2·D·a·d/(a+d))
        double vp = Math.Sqrt(2 * 0.2 * 0.5 * 0.6 / 1.1);
        Assert.Equal(vp / 0.5 + vp / 0.6, Kinematics.PointToPointTime(0.2, 0.6, 0.5, 0.6), 9);
        Assert.True(vp < 0.6);
    }

    [Fact]
    public void TheoreticalProductivity_Formulas()
    {
        double pph = TheoreticalProductivity.PackagesPerHour(0.6, 12);
        Assert.Equal(180, pph, 9);
        Assert.Equal(540, TheoreticalProductivity.TonnesPerHour(pph, 3000), 9);
        Assert.Equal(20, Kinematics.TraversalTime(12, 0.6), 9);
    }
}
