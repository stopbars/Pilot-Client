namespace BARS_Client_V2.Infrastructure.Simulators.XPlane;

internal sealed class XPlaneRemovalMismatchException(string message)
    : InvalidOperationException(message);
