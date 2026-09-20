using System.IO;
using static BARS_Client_V2.Infrastructure.Simulators.XPlane.XPlaneAptRangePatcher;

namespace BARS_Client_V2.Infrastructure.Simulators.XPlane;

internal sealed class XPlaneAptGeometryMatcher
{
    internal const double ToleranceMetres = 0.1;
    private const double ApproximationMetres = 0.0001;
    private const int MaxPieces = 32768;
    private readonly Dictionary<RunPath, Prepared?> _prepared = [];

    private sealed record Piece(int Segment, double From, double To, Point A, Point D, double Start, double End);
    private sealed class Group(string[] styles)
    {
        public string[] Styles { get; } = styles;
        public List<Piece> Pieces { get; } = [];
        public double Length => Pieces.Count == 0 ? 0 : Pieces[^1].End;
        public double LengthError { get; set; }
        public double CurveError { get; set; }
    }
    private sealed record Prepared(Segment[] Segments, double[] Starts, Group[] Groups)
    {
        public double Length => Starts[^1];
    }

    internal IReadOnlyList<double[]>? Match(RunPath reference, RunPath installed, IReadOnlyList<double[]> ranges)
    {
        if (reference.Code != installed.Code || reference.Closed != installed.Closed || reference.Segments.Length == 0 || installed.Segments.Length == 0 ||
            Distance(reference.Segments[0].Curve.A, installed.Segments[0].Curve.A) > ToleranceMetres ||
            Distance(reference.Segments[^1].Curve.D, installed.Segments[^1].Curve.D) > ToleranceMetres) return null;
        var source = Prepare(reference);
        var target = Prepare(installed);
        if (source == null || target == null || source.Groups.Length != target.Groups.Length) return null;
        for (var index = 0; index < source.Groups.Length; index++)
            if (!Matches(source.Groups[index], target.Groups[index])) return null;

        // Artifact fractions use the patcher's distance tables. Convert through curve positions
        // so a straight path re-exported as a Bezier keeps the same removal boundaries.
        double Map(double fraction)
        {
            if (fraction is 0 or 1) return fraction;
            var distance = fraction * source.Length;
            var segmentIndex = 0;
            while (segmentIndex < source.Segments.Length - 1 && source.Starts[segmentIndex + 1] < distance) segmentIndex++;
            var parameter = source.Segments[segmentIndex].ParameterAt(distance - source.Starts[segmentIndex]);
            for (var groupIndex = 0; groupIndex < source.Groups.Length; groupIndex++)
            {
                var group = source.Groups[groupIndex];
                var piece = group.Pieces.FirstOrDefault(p => p.Segment == segmentIndex && parameter >= p.From && parameter <= p.To);
                if (piece == null) continue;
                var point = source.Segments[segmentIndex].Curve.At(parameter);
                var arc = piece.Start + Fraction(point, piece.A, piece.D) * (piece.End - piece.Start);
                var other = target.Groups[groupIndex];
                var targetArc = Math.Clamp(arc / group.Length, 0, 1) * other.Length;
                var targetPiece = At(other, targetArc);
                var part = Math.Clamp((targetArc - targetPiece.Start) / (targetPiece.End - targetPiece.Start), 0, 1);
                var targetSegment = target.Segments[targetPiece.Segment];
                var low = targetPiece.From;
                var high = targetPiece.To;
                for (var step = 0; step < 48; step++)
                {
                    var middle = (low + high) / 2;
                    if (Fraction(targetSegment.Curve.At(middle), targetPiece.A, targetPiece.D) < part) low = middle;
                    else high = middle;
                }
                var t = (low + high) / 2;
                var tableIndex = t * (targetSegment.Distances.Length - 1);
                var start = Math.Min((int)tableIndex, targetSegment.Distances.Length - 2);
                var metric = targetSegment.Distances[start] + (tableIndex - start) * (targetSegment.Distances[start + 1] - targetSegment.Distances[start]);
                return (target.Starts[targetPiece.Segment] + metric) / target.Length;
            }
            throw new InvalidDataException("Unable to map an X-Plane removal boundary.");
        }
        var mapped = new List<double[]>();
        foreach (var range in ranges)
        {
            if (range.Length != 2 || !double.IsFinite(range[0]) || !double.IsFinite(range[1]) || range[0] < 0 || range[1] > 1 || range[0] >= range[1])
                throw new InvalidDataException("Invalid apt.dat removal range.");
            var from = Map(range[0]);
            var to = Map(range[1]);
            if (!double.IsFinite(from) || !double.IsFinite(to) || from < 0 || to > 1 || from >= to) return null;
            mapped.Add([from, to]);
        }
        return mapped;
    }

    private Prepared? Prepare(RunPath path)
    {
        if (_prepared.TryGetValue(path, out var cached)) return cached;
        Prepared? result;
        try { result = Create(path); }
        catch (InvalidDataException) { result = null; }
        _prepared.Add(path, result);
        return result;
    }

    private static Prepared Create(RunPath path)
    {
        if (path.Code is < 101 or > 108 || path.Segments.Length is 0 or > 4096) throw new InvalidDataException("Invalid lighting path.");
        var segments = path.Segments.Select(s => new Segment(s.Curve, s.Styles)).ToArray();
        var starts = new double[segments.Length + 1];
        var groups = new List<Group>();
        var pieceCount = 0;
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            var curve = segment.Curve;
            if (new[] { curve.A, curve.B, curve.C, curve.D }.Any(p => !double.IsFinite(p.Lat) || !double.IsFinite(p.Lon) || Math.Abs(p.Lat) > 90 || Math.Abs(p.Lon) > 180) ||
                segment.Length <= 0 || (index > 0 && segments[index - 1].Curve.D != curve.A) ||
                !segment.Styles.Contains(path.Code.ToString(System.Globalization.CultureInfo.InvariantCulture))) throw new InvalidDataException("Invalid lighting path.");
            starts[index + 1] = starts[index] + segment.Length;
            var styles = segment.Styles.Order(StringComparer.Ordinal).ToArray();
            if (groups.Count == 0 || !groups[^1].Styles.SequenceEqual(styles)) groups.Add(new Group(styles));
            var group = groups[^1];
            void Flatten(Curve part, double from, double to, double errorBudget, int depth)
            {
                if (++pieceCount > MaxPieces || depth > 24) throw new InvalidDataException("Lighting curve exceeds matching precision limits.");
                var chord = Distance(part.A, part.D);
                var excess = part.Curved ? Math.Max(0, Distance(part.A, part.B) + Distance(part.B, part.C) + Distance(part.C, part.D) - chord) : 0;
                var deviation = part.Curved ? Math.Max(OffChord(part.B, part.A, part.D), OffChord(part.C, part.A, part.D)) : 0;
                if (excess > errorBudget || deviation > ApproximationMetres)
                {
                    var middle = (from + to) / 2;
                    Flatten(part.Slice(0, .5), from, middle, errorBudget / 2, depth + 1);
                    Flatten(part.Slice(.5, 1), middle, to, errorBudget / 2, depth + 1);
                    return;
                }
                if (chord <= 0) throw new InvalidDataException("Degenerate lighting curve.");
                group.Pieces.Add(new Piece(index, from, to, part.A, part.D, group.Length, group.Length + chord));
                group.LengthError += excess;
                group.CurveError = Math.Max(group.CurveError, deviation);
            }
            Flatten(curve, 0, 1, ApproximationMetres / segments.Length, 0);
        }
        if (path.Closed && segments[^1].Curve.D != segments[0].Curve.A) throw new InvalidDataException("Unclosed lighting path.");
        return new Prepared(segments, starts, groups.ToArray());
    }

    private static bool Matches(Group source, Group target)
    {
        if (!source.Styles.SequenceEqual(target.Styles) || Math.Abs(source.Length - target.Length) > 2 * ToleranceMetres) return false;
        var error = source.CurveError + target.CurveError + 2 * (source.LengthError + target.LengthError);
        var limit = ToleranceMetres - error;
        if (limit <= 0) return false;
        // Between the merged polyline breakpoints both paths are linear. Their separation
        // is bounded by the endpoints, with the curve/length approximation error reserved.
        var fractions = source.Pieces.Select(p => p.End / source.Length).Concat(target.Pieces.Select(p => p.End / target.Length)).Prepend(0d).Distinct().Order();
        foreach (var fraction in fractions)
        {
            var a = Position(source, fraction * source.Length);
            var b = Position(target, fraction * target.Length);
            if (Distance(a, b) > limit) return false;
        }
        return true;
    }

    private static Piece At(Group group, double arc)
    {
        var low = 0;
        var high = group.Pieces.Count - 1;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (group.Pieces[middle].End < arc) low = middle + 1;
            else high = middle;
        }
        return group.Pieces[low];
    }

    private static Point Position(Group group, double arc)
    {
        var piece = At(group, arc);
        return Point.Lerp(piece.A, piece.D, Math.Clamp((arc - piece.Start) / (piece.End - piece.Start), 0, 1));
    }

    private static double Fraction(Point p, Point a, Point d)
    {
        var cosine = Math.Cos(a.Lat * Math.PI / 180);
        var x = (p.Lon - a.Lon) * cosine;
        var y = p.Lat - a.Lat;
        var dx = (d.Lon - a.Lon) * cosine;
        var dy = d.Lat - a.Lat;
        return Math.Clamp((x * dx + y * dy) / (dx * dx + dy * dy), 0, 1);
    }

    private static double OffChord(Point p, Point a, Point d) => a == d ? Distance(p, a) : Distance(p, Point.Lerp(a, d, Fraction(p, a, d)));
}
