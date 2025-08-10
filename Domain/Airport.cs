namespace BARS_Client_V2.Domain;

/// <summary>
/// Basic airport domain model containing ICAO code and available scenery packages.
/// </summary>
public sealed record SceneryPackage(string Name);

public sealed record Airport(string ICAO, IReadOnlyList<SceneryPackage> SceneryPackages);
