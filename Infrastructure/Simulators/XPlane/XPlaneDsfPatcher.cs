using System.IO;
using System.Text.Json.Serialization;
using SharpCompress.Archives.SevenZip;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BARS_Client_V2.Infrastructure.Simulators.XPlane;

internal sealed class XPlaneDsfSelector
{
    [JsonRequired]
    public string Kind { get; set; } = "";
    [JsonRequired]
    public string Source { get; set; } = "";
    [JsonRequired]
    public string Sha256 { get; set; } = "";
    [JsonRequired]
    public string Definition { get; set; } = "";
    [JsonRequired]
    public int Command { get; set; }
    [JsonRequired]
    public int Pool { get; set; }
    [JsonRequired]
    public int Filter { get; set; }
    [JsonRequired]
    public int Index { get; set; }
    public string? Geometry { get; set; }

    public void Validate()
    {
        if (!XPlaneRemovalGeometry.IsValid(Geometry) || Kind is not ("dsf-string" or "dsf-object") ||
            !Regex.IsMatch(Source, @"^Earth nav data/[+-]\d{2}[+-]\d{3}/[+-]\d{2}[+-]\d{3}\.dsf$") ||
            !Regex.IsMatch(Sha256, "^[a-f0-9]{64}$") || Definition.Length is 0 or > 512 ||
            Definition.Any(c => char.IsControl(c) || "<>\"&".Contains(c)) ||
            !Definition.EndsWith(Kind == "dsf-string" ? ".str" : ".obj", StringComparison.Ordinal) ||
            Command < 12 || Pool is < 0 or > 65535 || Filter < -1 || Index is < 0 or > 65535)
            throw new InvalidDataException("Invalid DSF removal selector.");
    }
}

internal static class XPlaneDsfPatcher
{
    private sealed record Atom(string Id, int Start, int End);
    private sealed record GeometryCandidate(XPlaneDsfSelector Selector, int Parameter, int[] Points);
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static byte[] Patch(byte[] bytes, IReadOnlyCollection<XPlaneDsfSelector> selectors)
    {
        if (selectors.Count == 0) return bytes;
        return Process(bytes, ResolveSelectors(bytes, selectors));
    }

    private static byte[] Process(byte[] bytes, IReadOnlyCollection<XPlaneDsfSelector> selectors,
        List<GeometryCandidate>? candidates = null, List<string>? comments = null, HashSet<string>? selectedDefinitions = null)
    {
        var sourceHash = Hash(bytes);
        foreach (var selector in selectors)
        {
            selector.Validate();
            if (sourceHash != selector.Sha256) throw new InvalidDataException("DSF source changed. Regenerate removals against the installed scenery.");
        }
        bytes = DecodeAndValidate(bytes);
        var atoms = ReadAtoms(bytes, 12, bytes.Length - 16);
        var commands = atoms.Single(a => a.Id == "CMDS");
        var definitions = atoms.Single(a => a.Id == "DEFN");
        var tables = ReadAtoms(bytes, definitions.Start + 8, definitions.End);
        string[] Table(string name)
        {
            var atom = tables.Single(a => a.Id == name);
            return Encoding.UTF8.GetString(bytes, atom.Start + 8, atom.End - atom.Start - 8).Split('\0');
        }
        var polygons = Table("POLY");
        var objects = Table("OBJT");
        var requests = selectors.GroupBy(s => s.Command).ToDictionary(g => g.Key, g => g.ToArray());
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        var cursor = commands.Start + 8;
        int pool = 0, definition = 0, filter = -1;
        int Take(int length)
        {
            if (length < 0 || length > commands.End - cursor) throw new InvalidDataException("Truncated DSF command.");
            var start = cursor; cursor += length; return start;
        }
        byte U8() => bytes[Take(1)];
        int U16() => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(Take(2)));
        int U32() => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(Take(4))));
        int[] Range(int start, int end) => end >= start ? Enumerable.Range(start, end - start).ToArray() : throw new InvalidDataException("Inverted DSF range.");
        while (cursor < commands.End)
        {
            var start = cursor;
            var op = U8();
            List<int[]>? windings = null;
            int[]? placements = null;
            var parameter = 0;
            switch (op)
            {
                case 1: pool = U16(); break;
                case 2: Take(4); break;
                case 3: definition = U8(); break;
                case 4: definition = U16(); break;
                case 5: definition = U32(); break;
                case 6: Take(1); break;
                case 7: placements = [U16()]; break;
                case 8: placements = Range(U16(), U16()); break;
                case 9: Take(U8() * 2); break;
                case 10: Take(4); break;
                case 11: Take(U8() * 4); break;
                case 12: parameter = U16(); windings = [Enumerable.Range(0, U8()).Select(_ => U16()).ToArray()]; break;
                case 13: parameter = U16(); windings = [Range(U16(), U16())]; break;
                case 14:
                    parameter = U16(); windings = []; var count = U8();
                    for (var i = 0; i < count; i++) windings.Add(Enumerable.Range(0, U8()).Select(_ => U16()).ToArray());
                    break;
                case 15:
                    parameter = U16(); windings = []; var windingCount = U8(); var first = U16();
                    for (var i = 0; i < windingCount; i++) { var end = U16(); windings.Add(Range(first, end)); first = end; }
                    break;
                case 16: break;
                case 17: Take(1); break;
                case 18: Take(9); break;
                case 23: case 26: case 29: Take(U8() * 2); break;
                case 24: case 27: case 30: Take(U8() * 4); break;
                case 25: case 28: case 31: Take(4); break;
                case 32: case 33: case 34:
                    var size = op == 32 ? U8() : op == 33 ? U16() : U32(); var position = Take(size);
                    if (size == 6 && BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position)) == 1)
                        filter = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position + 2));
                    else comments?.Add(XPlaneRemovalGeometry.Hex(bytes.AsSpan(position, size).ToArray()));
                    break;
                default: throw new InvalidDataException($"Unsupported DSF command {op}.");
            }
            if (candidates != null)
            {
                var table = placements != null ? objects : polygons;
                if ((placements != null || windings != null) && definition < table.Length && selectedDefinitions!.Contains(table[definition]))
                {
                    void Add(string kind, int index, int[] points) => candidates.Add(new(new XPlaneDsfSelector
                    {
                        Kind = kind, Sha256 = sourceHash, Definition = table[definition], Command = start,
                        Pool = pool, Filter = filter, Index = index
                    }, parameter, points));
                    if (placements != null) foreach (var index in placements) Add("dsf-object", index, [index]);
                    if (windings != null) for (var i = 0; i < windings.Count; i++) Add("dsf-string", i, windings[i]);
                    if (candidates.Count > 200000) throw new InvalidDataException("Too many DSF geometry candidates.");
                }
                continue;
            }
            if (!requests.Remove(start, out var selections)) { writer.Write(bytes, start, cursor - start); continue; }
            foreach (var selection in selections)
            {
                var table = placements != null ? objects : polygons;
                if (selection.Pool != pool || selection.Filter != filter || definition >= table.Length || table[definition] != selection.Definition)
                    throw new InvalidDataException("DSF definition, point pool or airport filter changed.");
                if (selection.Kind == "dsf-string" && (windings == null || selection.Index >= windings.Count) ||
                    selection.Kind == "dsf-object" && (placements == null || !placements.Contains(selection.Index)))
                    throw new InvalidDataException("DSF selection is not present in its source command.");
            }
            var removed = selections.Select(s => s.Index).ToHashSet();
            if (placements != null)
            {
                foreach (var index in placements.Where(index => !removed.Contains(index))) { writer.Write((byte)7); writer.Write((ushort)index); }
            }
            else if (windings != null)
            {
                for (var i = 0; i < windings.Count; i++)
                {
                    if (removed.Contains(i)) continue;
                    var points = windings[i];
                    if (points.Length <= 255)
                    {
                        writer.Write((byte)12); writer.Write((ushort)parameter); writer.Write((byte)points.Length);
                        foreach (var index in points) writer.Write((ushort)index);
                    }
                    else if (points[^1] < 65535 && points.Select((value, index) => value == points[0] + index).All(value => value))
                    {
                        writer.Write((byte)13); writer.Write((ushort)parameter); writer.Write((ushort)points[0]); writer.Write((ushort)(points[^1] + 1));
                    }
                    else throw new InvalidDataException("Cannot preserve the remaining DSF winding encoding.");
                }
            }
            else throw new InvalidDataException("DSF selector targets a state command.");
        }
        if (candidates != null) return bytes;
        if (requests.Count != 0) throw new InvalidDataException("DSF selection command was not found.");
        using var result = new MemoryStream();
        using var resultWriter = new BinaryWriter(result, Encoding.UTF8, leaveOpen: true);
        resultWriter.Write(bytes, 0, commands.Start + 4);
        resultWriter.Write(checked((uint)(output.Length + 8)));
        resultWriter.Write(output.ToArray());
        resultWriter.Write(bytes, commands.End, bytes.Length - 16 - commands.End);
        resultWriter.Flush();
        var body = result.ToArray();
        resultWriter.Write(MD5.HashData(body));
        return result.ToArray();
    }

    public static XPlaneDsfSelector[] ResolveSelectors(byte[] bytes, IReadOnlyCollection<XPlaneDsfSelector> selectors, bool captureGeometry = false)
    {
        var sourceHash = Hash(bytes);
        if (!captureGeometry) selectors = selectors.Select(WithReferenceGeometry).ToArray();
        foreach (var selector in selectors)
        {
            selector.Validate();
            if (sourceHash != selector.Sha256 && selector.Geometry == null)
                throw new XPlaneRemovalMismatchException("DSF source changed and has no exact geometry metadata. Regenerate removals for this scenery.");
        }
        if (!captureGeometry && selectors.All(selector => selector.Geometry == null)) return selectors.ToArray();
        var decoded = Decode(bytes);
        List<GeometryCandidate> candidates = [];
        List<string> comments = [];
        Process(decoded, [], candidates, comments, selectors.Select(selector => selector.Definition).ToHashSet(StringComparer.Ordinal));
        var atoms = ReadAtoms(decoded, 12, decoded.Length - 16);
        var head = atoms.Single(atom => atom.Id == "HEAD");
        var properties = ReadAtoms(decoded, head.Start + 8, head.End).Single(atom => atom.Id == "PROP");
        var context = XPlaneRemovalGeometry.DsfContext(decoded.AsSpan(properties.Start + 8, properties.End - properties.Start - 8).ToArray(), comments);
        var geodata = atoms.Single(atom => atom.Id == "GEOD");
        var poolAtoms = ReadAtoms(decoded, geodata.Start + 8, geodata.End);
        var pools = poolAtoms.Where(atom => atom.Id == "POOL").ToArray();
        var scales = poolAtoms.Where(atom => atom.Id == "SCAL").ToArray();
        var decodedPools = new Dictionary<int, double[][]>();
        foreach (var candidate in candidates)
        {
            var pool = candidate.Selector.Pool;
            if (pool >= pools.Length || pool >= scales.Length) throw new InvalidDataException("Missing DSF point pool.");
            if (!decodedPools.TryGetValue(pool, out var points))
                decodedPools.Add(pool, points = ReadPool(decoded, pools[pool], scales[pool]));
            if (candidate.Points.Any(index => index >= points.Length)) throw new InvalidDataException("Missing DSF point.");
            candidate.Selector.Geometry = XPlaneRemovalGeometry.DsfFingerprint(context, candidate.Selector.Kind,
                candidate.Selector.Definition, candidate.Selector.Filter, candidate.Parameter, candidate.Points.Select(index => points[index]).ToArray());
            candidate.Selector.Sha256 = sourceHash;
        }
        var byGeometry = candidates.ToLookup(candidate => candidate.Selector.Geometry);
        var byLocation = candidates.ToLookup(candidate => (candidate.Selector.Command, candidate.Selector.Pool, candidate.Selector.Filter, candidate.Selector.Index));
        List<XPlaneDsfSelector> resolved = [];
        var owners = new Dictionary<(int Command, int Index), XPlaneDsfSelector>();
        foreach (var selector in selectors)
        {
            if (selector.Geometry == null && !captureGeometry) { resolved.Add(selector); continue; }
            var matches = sourceHash == selector.Sha256
                ? byLocation[(selector.Command, selector.Pool, selector.Filter, selector.Index)].ToArray()
                : byGeometry[selector.Geometry].ToArray();
            if (matches.Length != 1 || (!captureGeometry && matches[0].Selector.Geometry != selector.Geometry) || matches[0].Selector.Kind != selector.Kind ||
                matches[0].Selector.Definition != selector.Definition)
                throw new XPlaneRemovalMismatchException("DSF removal did not find one exact geometry and property match. Scenery was left unchanged.");
            var match = matches[0].Selector;
            if (owners.TryGetValue((match.Command, match.Index), out var owner) &&
                (owner.Command != selector.Command || owner.Index != selector.Index || owner.Sha256 != selector.Sha256))
                throw new XPlaneRemovalMismatchException("Multiple DSF selections resolve to the same installed geometry.");
            owners[(match.Command, match.Index)] = selector;
            match.Source = selector.Source;
            resolved.Add(match);
        }
        return resolved.ToArray();
    }

    internal static XPlaneDsfSelector WithReferenceGeometry(XPlaneDsfSelector selector) => new()
    {
        Kind = selector.Kind, Source = selector.Source, Sha256 = selector.Sha256, Definition = selector.Definition,
        Command = selector.Command, Pool = selector.Pool, Filter = selector.Filter, Index = selector.Index,
        Geometry = selector.Geometry ?? XPlaneRemovalReferences.Dsf(selector)
    };

    private static double[][] ReadPool(byte[] bytes, Atom pool, Atom scaleAtom)
    {
        var cursor = pool.Start + 8;
        int Take(int count)
        {
            if (count < 0 || count > pool.End - cursor) throw new InvalidDataException("Truncated DSF point pool.");
            var start = cursor; cursor += count; return start;
        }
        var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(Take(4))));
        var planes = bytes[Take(1)];
        if (count is < 1 or > 65536 || planes is < 1 or > 32 || scaleAtom.End - scaleAtom.Start - 8 != planes * 8)
            throw new InvalidDataException("Invalid DSF point pool dimensions.");
        var points = Enumerable.Range(0, count).Select(_ => new double[planes]).ToArray();
        for (var plane = 0; plane < planes; plane++)
        {
            var encoding = bytes[Take(1)];
            if (encoding > 3) throw new InvalidDataException("Unsupported DSF point encoding.");
            var scale = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(scaleAtom.Start + 8 + plane * 8));
            var offset = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(scaleAtom.Start + 12 + plane * 8));
            if (!float.IsFinite(scale) || !float.IsFinite(offset)) throw new InvalidDataException("Invalid DSF point scale.");
            int index = 0, previous = 0;
            int Read() => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(Take(2)));
            void Add(int value)
            {
                if (index >= count) throw new InvalidDataException("DSF point run exceeds its pool.");
                if ((encoding & 1) != 0) value = (value + previous) & 65535;
                previous = value;
                points[index++][plane] = scale == 0 ? value : (value * (double)scale) / 65535 + offset;
            }
            while (index < count)
            {
                if (encoding < 2) { Add(Read()); continue; }
                var run = bytes[Take(1)];
                var length = run & 127;
                if (length == 0 || length > count - index) throw new InvalidDataException("Invalid DSF point run.");
                if ((run & 128) != 0) { var value = Read(); for (var i = 0; i < length; i++) Add(value); }
                else for (var i = 0; i < length; i++) Add(Read());
            }
        }
        if (cursor != pool.End) throw new InvalidDataException("Unexpected DSF point data.");
        return points;
    }

    internal static bool HasSameSceneryContent(byte[] original, byte[] current)
    {
        original = DecodeAndValidate(original);
        current = DecodeAndValidate(current);
        bool Compare(List<Atom> before, List<Atom> after, bool inHead = false)
        {
            if (before.Count != after.Count) return false;
            for (var index = 0; index < before.Count; index++)
            {
                var a = before[index];
                var b = after[index];
                if (a.Id != b.Id) return false;
                if (!inHead && a.Id == "HEAD")
                {
                    if (!Compare(ReadAtoms(original, a.Start + 8, a.End), ReadAtoms(current, b.Start + 8, b.End), true)) return false;
                }
                else if (inHead && a.Id == "PROP")
                {
                    if (!WithoutExporter(original.AsSpan(a.Start + 8, a.End - a.Start - 8)).AsSpan()
                        .SequenceEqual(WithoutExporter(current.AsSpan(b.Start + 8, b.End - b.Start - 8)))) return false;
                }
                else if (!original.AsSpan(a.Start, a.End - a.Start).SequenceEqual(current.AsSpan(b.Start, b.End - b.Start))) return false;
            }
            return true;
        }
        // Compare the entire pristine file, not just the lights selected for removal.
        return Compare(ReadAtoms(original, 12, original.Length - 16), ReadAtoms(current, 12, current.Length - 16));
    }

    private static byte[] WithoutExporter(ReadOnlySpan<byte> properties)
    {
        using var output = new MemoryStream();
        while (!properties.IsEmpty)
        {
            var keyEnd = properties.IndexOf((byte)0);
            if (keyEnd <= 0) throw new InvalidDataException("Invalid DSF property key.");
            var valueEnd = properties[(keyEnd + 1)..].IndexOf((byte)0);
            if (valueEnd < 0) throw new InvalidDataException("Invalid DSF property value.");
            var length = keyEnd + valueEnd + 2;
            if (!properties[..keyEnd].SequenceEqual("sim/creation_agent"u8)) output.Write(properties[..length]);
            properties = properties[length..];
        }
        return output.ToArray();
    }

    private static byte[] DecodeAndValidate(byte[] bytes)
    {
        bytes = Decode(bytes);
        if (bytes.Length < 28 || Encoding.ASCII.GetString(bytes, 0, 8) != "XPLNEDSF" || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) != 1)
            throw new InvalidDataException("Expected an uncompressed DSF version 1 file.");
        if (!MD5.HashData(bytes.AsSpan(0, bytes.Length - 16)).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - 16)))
            throw new InvalidDataException("DSF checksum mismatch.");
        return bytes;
    }

    private static byte[] Decode(byte[] bytes)
    {
        if (!bytes.AsSpan().StartsWith(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c })) return bytes;
        if (bytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("Compressed DSF exceeds 64 MB.");
        using var input = new MemoryStream(bytes, writable: false);
        using var archive = SevenZipArchive.OpenArchive(input);
        var entries = archive.Entries.ToArray();
        if (entries.Length != 1 || entries[0].IsDirectory || entries[0].IsEncrypted || entries[0].Size is < 28 or > 268435456)
            throw new InvalidDataException("A compressed DSF must contain one unencrypted file no larger than 256 MB.");
        using var stream = entries[0].OpenEntryStream();
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            if (output.Length + count > entries[0].Size) throw new InvalidDataException("Compressed DSF exceeds its declared size.");
            output.Write(buffer, 0, count);
        }
        if (output.Length != entries[0].Size) throw new InvalidDataException("Truncated compressed DSF.");
        return output.ToArray();
    }

    private static List<Atom> ReadAtoms(byte[] bytes, int start, int end)
    {
        List<Atom> atoms = [];
        while (start < end)
        {
            if (end - start < 8) throw new InvalidDataException("Truncated DSF atom.");
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(start + 4));
            if (size < 8 || size > end - start) throw new InvalidDataException("Invalid DSF atom size.");
            atoms.Add(new Atom(new string(Encoding.ASCII.GetString(bytes, start, 4).Reverse().ToArray()), start, start + (int)size));
            start += (int)size;
        }
        return atoms;
    }
}
