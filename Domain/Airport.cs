namespace BARS_Client_V2.Domain;

public sealed record SceneryPackage(
    string Name,
    string? ArtifactIdentity = null,
    string? ArtifactGenerationId = null,
    string? RemovalArtifactKey = null);

public sealed record Airport(string ICAO, string? Name, IReadOnlyList<SceneryPackage> SceneryPackages);
