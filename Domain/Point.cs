namespace BARS_Client_V2.Domain;

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

public sealed record PointState(PointMetadata Metadata, bool IsOn, long TimestampMs);
