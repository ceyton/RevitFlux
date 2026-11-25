using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RevitFlux.Core.Geometry;

/// <summary>One segment of a path. An arc passes through (Mx, My).</summary>
public readonly struct Segment2D
{
    public Segment2D(double x0, double y0, double x1, double y1, bool isArc, double mx, double my)
    {
        X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; IsArc = isArc; Mx = mx; My = my;
    }

    public double X0 { get; }
    public double Y0 { get; }
    public double X1 { get; }
    public double Y1 { get; }
    public bool IsArc { get; }
    public double Mx { get; }
    public double My { get; }
}

/// <summary>
/// The path format shared by the geometry tools: a list of vertices, each [x,y], or [x,y,mx,my]
/// when the segment that starts there is an arc through (mx,my).
/// </summary>
public static class Path2D
{
    public const string Help = "a path is [[x,y],...]; use [x,y,mx,my] where the segment starting there is an arc through (mx,my)";

    public static List<Segment2D> Parse(JToken? token, bool closed, string name)
    {
        if (token is not JArray vertices || vertices.Count < 2)
            throw new ArgumentException($"{name}: {Help}");

        var points = new List<double[]>();
        foreach (var v in vertices) points.Add(Numbers(v, name));
        foreach (var p in points)
        {
            if (p.Length != 2 && p.Length != 4) throw new ArgumentException($"{name}: {Help}");
        }

        // Accept a closed path that repeats its first point.
        var last = points[points.Count - 1];
        if (closed && points.Count > 2 && Near(points[0][0], last[0]) && Near(points[0][1], last[1]))
            points.RemoveAt(points.Count - 1);

        var segments = new List<Segment2D>();
        var count = closed ? points.Count : points.Count - 1;
        for (var i = 0; i < count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            var arc = a.Length == 4;
            segments.Add(new Segment2D(a[0], a[1], b[0], b[1], arc, arc ? a[2] : 0, arc ? a[3] : 0));
        }
        if (closed && segments.Count < 3 && !segments.Exists(s => s.IsArc))
            throw new ArgumentException($"{name}: a closed path needs 3 points, or 2 with an arc");
        return segments;
    }

    public static double[] Numbers(JToken? token, string name, int min = 2)
    {
        if (token is not JArray a || a.Count < min) throw new ArgumentException($"{name}: expected at least {min} numbers");
        var result = new double[a.Count];
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Type != JTokenType.Integer && a[i].Type != JTokenType.Float)
                throw new ArgumentException($"{name}: expected numbers");
            result[i] = a[i].Value<double>();
        }
        return result;
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.001;
}
