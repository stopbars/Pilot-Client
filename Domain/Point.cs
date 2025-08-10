namespace BARS_Client_V2.Domain;

/// <summary>
/// Metadata for a controllable airfield object (stopbar / light / etc.).
/// </summary>
public sealed record PointMetadata(
    string Id,
    string AirportId,
    string Type,
    string Name,
    double Latitude,
    double Longitude,
    string? Directionality,
    string? Orientation,
    string? Color,
    bool Elevated,
    bool Ihp
);

/// <summary>
/// Current dynamic state of a point (on/off) plus metadata.
/// </summary>
public sealed record PointState(PointMetadata Metadata, bool IsOn, long TimestampMs);
