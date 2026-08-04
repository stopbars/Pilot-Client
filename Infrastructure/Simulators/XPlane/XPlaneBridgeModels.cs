namespace BARS_Client_V2.Infrastructure.Simulators.XPlane;

public sealed record XPlaneBridgeLight(
    string Id,
    double Latitude,
    double Longitude,
    double Heading,
    int StateId,
    bool Visible = true);

public sealed record XPlaneBridgeLightPatch(
    string Id,
    int? StateId = null,
    bool? Visible = null);

