namespace BARS_Client_V2.Domain;

public sealed record SceneryPackage(string Name);

public sealed record Airport(string ICAO, IReadOnlyList<SceneryPackage> SceneryPackages);
