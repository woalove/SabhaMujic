namespace SabhaMujic.Audio.Mixing;

/// <summary>
/// Equal-power crossfade gains: <c>out = cos(t·π/2)</c>, <c>in = sin(t·π/2)</c> for t in [0, 1].
/// For uncorrelated signals of equal power the summed power stays constant across the fade.
/// </summary>
public static class EqualPowerCrossfade
{
    public static float FadeOutGain(double t) => (float)Math.Cos(Math.Clamp(t, 0, 1) * (Math.PI / 2));

    public static float FadeInGain(double t) => (float)Math.Sin(Math.Clamp(t, 0, 1) * (Math.PI / 2));

    /// <summary>
    /// Gain correction for partially correlated material (loop seams): with correlation ρ the equal-power sum has power
    /// <c>1 + 2ρ·cos·sin</c>, so both gains are divided by its square root. ρ = 0 leaves the pure equal-power curve.
    /// </summary>
    public static float CorrelationCompensation(double t, double correlation)
    {
        if (correlation <= 0)
        {
            return 1f;
        }

        double angle = Math.Clamp(t, 0, 1) * (Math.PI / 2);
        double power = 1 + 2 * Math.Min(1, correlation) * Math.Cos(angle) * Math.Sin(angle);
        return (float)(1 / Math.Sqrt(power));
    }
}
