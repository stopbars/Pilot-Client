using System.IO;
using System.Text.Json;

namespace BARS_Client_V2.Infrastructure.Simulators.XPlane;

internal static class XPlaneRemovalReferences
{
    private const string ResourceName = "BARS.XPlaneRemovalReferences.json";
    private static readonly Lazy<Catalog> Reference = new(Load);

    public static string? Apt(string icao, string feature) =>
        Reference.Value.Apt.GetValueOrDefault(icao.ToUpperInvariant())?.GetValueOrDefault(feature.ToLowerInvariant());

    public static string? Dsf(XPlaneDsfSelector selector) => Reference.Value.DsfIndex.GetValueOrDefault(Key(selector));
    public static string? AptRun(string icao, string feature, int code, int run) =>
        Reference.Value.AptRuns.GetValueOrDefault(icao.ToUpperInvariant())?.GetValueOrDefault(
            RunKey(feature, code, run));

    public static XPlaneAptRangePatcher.RunPath? AptPath(string icao, string feature, int code, int run) =>
        Reference.Value.AptPaths.GetValueOrDefault(icao.ToUpperInvariant())?.GetValueOrDefault(RunKey(feature, code, run));

    private static string RunKey(string feature, int code, int run) => feature.ToLowerInvariant() + ":" +
        code.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + run.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Key(XPlaneDsfSelector selector) => string.Join('\n', selector.Sha256, selector.Source,
        selector.Kind, selector.Definition, selector.Command.ToString(System.Globalization.CultureInfo.InvariantCulture),
        selector.Pool.ToString(System.Globalization.CultureInfo.InvariantCulture), selector.Filter.ToString(System.Globalization.CultureInfo.InvariantCulture),
        selector.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static Catalog Load()
    {
        using var stream = typeof(XPlaneRemovalReferences).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("X-Plane removal reference data is missing.");
        var catalog = JsonSerializer.Deserialize<Catalog>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("X-Plane removal reference data is invalid.");
        if (catalog.Schema != "bars-xplane-geometry-references/v1") throw new InvalidDataException("Unsupported X-Plane removal references.");
        foreach (var airport in catalog.Apt.Values.Concat(catalog.AptRuns.Values))
            foreach (var geometry in airport.Values)
                if (geometry == null || !XPlaneRemovalGeometry.IsValid(geometry)) throw new InvalidDataException("Invalid apt geometry reference.");
        foreach (var selector in catalog.Dsf)
        {
            selector.Validate();
            if (selector.Geometry == null || !catalog.DsfIndex.TryAdd(Key(selector), selector.Geometry))
                throw new InvalidDataException("Invalid or duplicate DSF geometry reference.");
        }
        foreach (var airport in catalog.AptPaths)
            foreach (var entry in airport.Value)
            {
                var path = entry.Value;
                if (!catalog.AptRuns.TryGetValue(airport.Key, out var runs) || !runs.TryGetValue(entry.Key, out var fingerprint) ||
                    entry.Key.Split(':') is not [_, var code, _] || code != path.Code.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                    XPlaneAptRangePatcher.RunFingerprint(path) != fingerprint)
                    throw new InvalidDataException("Original X-Plane path does not match its verified fingerprint.");
            }
        return catalog;
    }

    private sealed class Catalog
    {
        public string Schema { get; set; } = "";
        public Dictionary<string, Dictionary<string, string>> Apt { get; set; } = [];
        public Dictionary<string, Dictionary<string, string>> AptRuns { get; set; } = [];
        public Dictionary<string, Dictionary<string, XPlaneAptRangePatcher.RunPath>> AptPaths { get; set; } = [];
        public List<XPlaneDsfSelector> Dsf { get; set; } = [];
        [System.Text.Json.Serialization.JsonIgnore]
        public Dictionary<string, string> DsfIndex { get; } = new(StringComparer.Ordinal);
    }
}
