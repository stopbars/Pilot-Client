namespace BARS_Client_V2.Application;

public sealed class LightDrawDistanceSettings
{
    public const int DefaultMeters = 500;
    public const int MinimumMeters = 250;
    public const int MaximumMeters = 10000;
    public const int StepMeters = 250;

    private int _meters = DefaultMeters;

    public int Meters => Volatile.Read(ref _meters);

    public void SetMeters(int meters) => Interlocked.Exchange(ref _meters, Normalize(meters));

    public static int Normalize(int meters)
    {
        if (meters <= 0) return DefaultMeters;
        var clamped = Math.Clamp(meters, MinimumMeters, MaximumMeters);
        return (int)Math.Round((double)clamped / StepMeters, MidpointRounding.AwayFromZero) * StepMeters;
    }
}
