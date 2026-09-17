using System.Globalization;
using System.IO;

namespace BARS_Client_V2.Infrastructure.Simulators.XPlane;

internal static class XPlaneAptRangePatcher
{
    internal sealed record Selection(int Code, int Run, IReadOnlyList<double[]> Ranges);
    private readonly record struct Point(double Lat, double Lon)
    {
        public static Point Lerp(Point a, Point b, double t) => new(a.Lat + (b.Lat - a.Lat) * t, a.Lon + (b.Lon - a.Lon) * t);
        public static Point Mirror(Point p, Point c) => new(2 * p.Lat - c.Lat, 2 * p.Lon - c.Lon);
    }
    private sealed record Node(int Code, Point Position, Point? Incoming, Point? Outgoing, string[] Styles);
    private sealed record Curve(Point A, Point B, Point C, Point D, bool Curved)
    {
        public Point At(double t)
        {
            if (!Curved) return Point.Lerp(A, D, t);
            var ab = Point.Lerp(A, B, t); var bc = Point.Lerp(B, C, t); var cd = Point.Lerp(C, D, t);
            return Point.Lerp(Point.Lerp(ab, bc, t), Point.Lerp(bc, cd, t), t);
        }
        private (Curve Left, Curve Right) Split(double t)
        {
            var ab = Point.Lerp(A, B, t); var bc = Point.Lerp(B, C, t); var cd = Point.Lerp(C, D, t);
            var abc = Point.Lerp(ab, bc, t); var bcd = Point.Lerp(bc, cd, t); var point = Point.Lerp(abc, bcd, t);
            return (new(A, ab, abc, point, true), new(point, bcd, cd, D, true));
        }
        public Curve Slice(double from, double to)
        {
            if (!Curved) return new(At(from), At(from), At(to), At(to), false);
            if (from == 0 && to == 1) return this;
            var prefix = to == 1 ? this : Split(to).Left;
            return from == 0 ? prefix : prefix.Split(from / to).Right;
        }
    }
    private sealed class Segment(Curve curve, string[] styles)
    {
        public Curve Curve { get; } = curve;
        public string[] Styles { get; } = styles;
        public List<(int Code, double Start, double End)> Removals { get; } = [];
        public double[] Distances { get; } = DistancesFor(curve);
        public double Length => Distances[^1];
        public double ParameterAt(double distance)
        {
            distance = Math.Clamp(distance, 0, Length);
            for (var i = 1; i < Distances.Length; i++)
                if (distance <= Distances[i]) return ((i - 1) + (Distances[i] > Distances[i - 1] ? (distance - Distances[i - 1]) / (Distances[i] - Distances[i - 1]) : 0)) / (Distances.Length - 1);
            return 1;
        }
    }

    public static List<string> Patch(IReadOnlyList<string> lines, IReadOnlyList<Selection> selections)
    {
        var raw = lines.Where(line => IsNode(line)).Select(Parse).ToArray();
        if (raw.Length < 2) throw new InvalidDataException("Invalid apt.dat linear feature.");
        List<Node> nodes = [];
        for (var index = 0; index < raw.Length; index++)
        {
            var first = raw[index]; var last = first;
            while (index + 1 < raw.Length && raw[index + 1].Position == first.Position && (last.Outgoing.HasValue != raw[index + 1].Outgoing.HasValue)) last = raw[++index];
            nodes.Add(last with { Incoming = first.Incoming });
        }
        if (nodes.Take(nodes.Count - 1).Any(node => node.Code is 113 or 114 or 115 or 116)) throw new InvalidDataException("Unexpected terminated apt.dat path.");
        var closed = nodes[^1].Code is 113 or 114;
        List<Segment> segments = [];
        for (var index = 0; index < nodes.Count - (closed ? 0 : 1); index++)
        {
            var a = nodes[index]; var d = nodes[(index + 1) % nodes.Count];
            segments.Add(new Segment(new Curve(a.Position, a.Outgoing ?? a.Position, d.Incoming ?? d.Position, d.Position, a.Outgoing.HasValue || d.Incoming.HasValue), a.Styles));
        }
        foreach (var selection in selections)
        {
            List<List<Segment>> runs = []; List<Segment>? run = null;
            foreach (var segment in segments)
            {
                if (!segment.Styles.Contains(selection.Code.ToString(CultureInfo.InvariantCulture))) { run = null; continue; }
                if (run == null) { run = []; runs.Add(run); }
                run.Add(segment);
            }
            // The Website does not emit zero-length runs.
            runs.RemoveAll(candidate => candidate.Sum(segment => segment.Length) <= 0);
            if (selection.Run < 0 || selection.Run >= runs.Count || selection.Ranges.Count == 0) throw new InvalidDataException("apt.dat lighting run was not found.");
            var selected = runs[selection.Run]; var total = selected.Sum(segment => segment.Length); var cursor = 0d;
            foreach (var segment in selected)
            {
                foreach (var range in selection.Ranges)
                {
                    if (range.Length != 2 || !double.IsFinite(range[0]) || !double.IsFinite(range[1]) || range[0] < 0 || range[1] > 1 || range[1] <= range[0]) throw new InvalidDataException("Invalid apt.dat removal range.");
                    var from = Math.Max(cursor, range[0] * total); var to = Math.Min(cursor + segment.Length, range[1] * total);
                    if (to > from) segment.Removals.Add((selection.Code, segment.ParameterAt(from - cursor), segment.ParameterAt(to - cursor)));
                }
                cursor += segment.Length;
            }
        }
        List<(Curve Curve, string[] Styles)> pieces = [];
        foreach (var segment in segments)
        {
            var cuts = segment.Removals.SelectMany(removal => new[] { removal.Start, removal.End }).Append(0d).Append(1d).Distinct().Order().ToArray();
            for (var i = 1; i < cuts.Length; i++)
            {
                var middle = (cuts[i - 1] + cuts[i]) / 2;
                var removed = segment.Removals.Where(removal => middle > removal.Start && middle < removal.End).Select(removal => removal.Code.ToString(CultureInfo.InvariantCulture)).ToHashSet();
                pieces.Add((segment.Curve.Slice(cuts[i - 1], cuts[i]), segment.Styles.Where(style => !removed.Contains(style)).ToArray()));
            }
        }
        var output = lines.TakeWhile(line => !IsNode(line)).ToList();
        for (var i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];
            var previous = i > 0 ? pieces[i - 1].Curve : closed ? pieces[^1].Curve : null;
            Emit(output, piece.Curve.A, previous?.Curved == true ? previous.C : null, piece.Curve.Curved ? piece.Curve.B : null, piece.Styles, closed && i == pieces.Count - 1 ? 113 : 111);
        }
        if (!closed)
        {
            var last = pieces[^1].Curve;
            Emit(output, last.D, last.Curved ? last.C : null, null, [], 115);
        }
        output.AddRange(lines.Reverse().TakeWhile(line => !IsNode(line)).Reverse());
        return output;
    }

    private static void Emit(List<string> lines, Point point, Point? incoming, Point? outgoing, string[] styles, int code)
    {
        // WED uses curved/straight/curved co-located nodes for independent handles.
        if (incoming.HasValue && outgoing.HasValue && Point.Mirror(point, incoming.Value) == outgoing.Value)
        { lines.Add(Line(code + 1, point, outgoing, styles)); return; }
        if (incoming.HasValue) lines.Add(Line(112, point, Point.Mirror(point, incoming.Value), styles));
        if (incoming.HasValue || !outgoing.HasValue) lines.Add(Line(outgoing.HasValue ? 111 : code, point, null, styles));
        if (outgoing.HasValue) lines.Add(Line(code + 1, point, outgoing, styles));
    }
    private static string Line(int code, Point point, Point? control, string[] styles) =>
        string.Join(' ', new[] { code.ToString(CultureInfo.InvariantCulture), Format(point.Lat), Format(point.Lon) }
            .Concat(control.HasValue ? new[] { Format(control.Value.Lat), Format(control.Value.Lon) } : [])
            .Concat(code is 115 or 116 ? [] : styles));
    private static string Format(double value) => value.ToString("0.##############", CultureInfo.InvariantCulture);
    private static bool IsNode(string line) => int.TryParse(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out var code) && code is >= 111 and <= 116;
    private static Node Parse(string line)
    {
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var code = int.Parse(fields[0], CultureInfo.InvariantCulture);
        double Number(int i) => i < fields.Length && double.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : throw new InvalidDataException("Invalid apt.dat coordinate.");
        var point = new Point(Number(1), Number(2));
        Point? control = code is 112 or 114 or 116 ? new Point(Number(3), Number(4)) : null;
        var styles = code is 115 or 116 ? [] : fields.Skip(control.HasValue ? 5 : 3).SelectMany(field => field.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        return new Node(code, point, control.HasValue ? Point.Mirror(point, control.Value) : null, control, styles);
    }
    private static double[] DistancesFor(Curve curve)
    {
        var steps = curve.Curved ? Math.Max(2, Math.Min(64, (int)Math.Ceiling((Distance(curve.A, curve.B) + Distance(curve.B, curve.C) + Distance(curve.C, curve.D)) / 5))) : 1;
        var distances = new double[steps + 1];
        for (var i = 1; i <= steps; i++) distances[i] = distances[i - 1] + Distance(curve.At((double)(i - 1) / steps), curve.At((double)i / steps));
        return distances;
    }
    private static double Distance(Point a, Point b)
    {
        var lat = (b.Lat - a.Lat) * Math.PI / 180; var lon = (b.Lon - a.Lon) * Math.PI / 180;
        var value = Math.Pow(Math.Sin(lat / 2), 2) + Math.Cos(a.Lat * Math.PI / 180) * Math.Cos(b.Lat * Math.PI / 180) * Math.Pow(Math.Sin(lon / 2), 2);
        return 6371008.8 * 2 * Math.Atan2(Math.Sqrt(value), Math.Sqrt(Math.Max(0, 1 - value)));
    }
}
