using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BARS_Client_V2.Infrastructure.Simulators.XPlane;

internal static class XPlaneRemovalGeometry
{
    public static bool IsValid(string? fingerprint) => fingerprint == null || Regex.IsMatch(fingerprint, "^[a-f0-9]{64}$");
    internal static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static string? AptFingerprint(IEnumerable<string> lines)
    {
        var records = lines.Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (records.Length < 3 || !Regex.IsMatch(records[0], @"^120(?:\s|$)")) return null;
        List<string> nodes = [];
        foreach (var record in records.Skip(1))
        {
            var fields = Regex.Split(record, @"[\s,]+");
            if (!int.TryParse(fields[0], out var code) || code is < 111 or > 116) return null;
            var coordinates = code is 112 or 114 or 116 ? 4 : 2;
            if (fields.Length < coordinates + 1 || fields.Skip(coordinates + 1).Any(value => !Regex.IsMatch(value, @"^\d+$"))) return null;
            var normalized = fields.Select(Decimal).ToArray();
            if (normalized.Any(value => value == null)) return null;
            nodes.Add(string.Join(' ', normalized));
        }
        return Digest("bars-apt-geometry-v1\n" + string.Join('\n', nodes));
    }

    private static string? Decimal(string value)
    {
        if (!Regex.IsMatch(value, @"^[+-]?\d+(?:\.\d+)?$")) return null;
        var parts = value.TrimStart('+', '-').Split('.');
        var integer = parts[0].TrimStart('0');
        if (integer.Length == 0) integer = "0";
        var fraction = parts.Length == 2 ? parts[1].TrimEnd('0') : "";
        var magnitude = integer + (fraction.Length > 0 ? "." + fraction : "");
        return value.StartsWith('-') && magnitude != "0" ? "-" + magnitude : magnitude;
    }

    internal static string DsfContext(byte[] properties, IEnumerable<string> comments)
    {
        var fields = Encoding.UTF8.GetString(properties).Split('\0');
        var retained = new List<string>();
        for (var index = 0; index + 1 < fields.Length; index += 2)
        {
            if (fields[index] == "sim/creation_agent") continue;
            retained.Add(fields[index]); retained.Add(fields[index + 1]);
        }
        var semanticProperties = Encoding.UTF8.GetBytes(string.Join('\0', retained) + "\0");
        return Digest("bars-dsf-context-v1\n" + Hex(semanticProperties) + "\n" + string.Join('\n', comments));
    }

    internal static string DsfFingerprint(string context, string kind, string definition, int filter, int parameter, IReadOnlyList<double[]> points)
    {
        if (points.Count == 0 || points.Any(point => point.Length == 0 || point.Any(value => !double.IsFinite(value))))
            throw new InvalidDataException("Invalid DSF geometry.");
        string NumberHex(double value)
        {
            var bytes = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(bytes, value == 0 ? 0 : value);
            return Hex(bytes);
        }
        return Digest(string.Join('\n', "bars-dsf-geometry-v1", context, kind, Hex(Encoding.UTF8.GetBytes(definition)),
            filter.ToString(CultureInfo.InvariantCulture), parameter.ToString(CultureInfo.InvariantCulture),
            string.Join('\n', points.Select(point => string.Join(',', point.Select(NumberHex))))));
    }
}
