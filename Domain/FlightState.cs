namespace BARS_Client_V2.Domain;

/// <summary>
/// Raw flight state (position + ground flag) before nearest-airport lookup.
/// </summary>
public sealed record FlightState(double Latitude, double Longitude, bool OnGround);
