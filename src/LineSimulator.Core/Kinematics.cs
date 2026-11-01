namespace LineSimulator.Core;

/// <summary>Conversioni di unità. Internamente il motore lavora in SI (m, s).</summary>
public static class Units
{
    public static double MmToM(double mm) => mm / 1000.0;
    public static double MToMm(double m) => m * 1000.0;
    public static double MsToS(double ms) => ms / 1000.0;
}

/// <summary>Formule cinematiche per moto rettilineo con rampe lineari (profilo trapezoidale).</summary>
public static class Kinematics
{
    /// <summary>Spazio di frenatura da v a 0 con decelerazione costante d: s = v²/(2d).</summary>
    public static double BrakingDistance(double speed, double deceleration)
    {
        if (speed <= 0) return 0;
        if (deceleration <= 0) return double.PositiveInfinity;
        return speed * speed / (2.0 * deceleration);
    }

    /// <summary>
    /// Spazio percorso dal rilevamento all'arresto: tratto a velocità costante durante il tempo
    /// di risposta del sensore + frenatura: s = v·t_r + v²/(2d).
    /// </summary>
    public static double StoppingDistance(double speed, double responseTimeS, double deceleration)
        => Math.Max(0, speed) * Math.Max(0, responseTimeS) + BrakingDistance(speed, deceleration);

    /// <summary>
    /// Tempo per percorrere la distanza D partendo e arrivando da fermo, con velocità massima vMax,
    /// accelerazione a e decelerazione d. Profilo trapezoidale o triangolare.
    /// </summary>
    public static double PointToPointTime(double distance, double vMax, double acceleration, double deceleration)
    {
        if (distance <= 0) return 0;
        if (vMax <= 0 || acceleration <= 0 || deceleration <= 0) return double.PositiveInfinity;
        double dAcc = vMax * vMax / (2 * acceleration);
        double dDec = vMax * vMax / (2 * deceleration);
        if (dAcc + dDec <= distance)
        {
            return vMax / acceleration + vMax / deceleration + (distance - dAcc - dDec) / vMax;
        }
        // Profilo triangolare: vPeak tale che vPeak²/(2a) + vPeak²/(2d) = D
        double vPeak = Math.Sqrt(2 * distance * acceleration * deceleration / (acceleration + deceleration));
        return vPeak / acceleration + vPeak / deceleration;
    }

    /// <summary>Tempo teorico di attraversamento a velocità costante: t = L / v.</summary>
    public static double TraversalTime(double lengthM, double speed)
        => speed > 0 ? lengthM / speed : double.PositiveInfinity;
}

/// <summary>Formule di produttività teorica (NON effettiva: la effettiva viene dalla simulazione).</summary>
public static class TheoreticalProductivity
{
    /// <summary>pacchi/ora = 3600 × v (m/s) / L pacco (m), senza spaziatura.</summary>
    public static double PackagesPerHour(double speed, double packageLengthM)
        => packageLengthM > 0 ? 3600.0 * speed / packageLengthM : 0;

    /// <summary>t/h = pacchi/ora × peso (kg) / 1000.</summary>
    public static double TonnesPerHour(double packagesPerHour, double weightKg)
        => packagesPerHour * weightKg / 1000.0;
}
