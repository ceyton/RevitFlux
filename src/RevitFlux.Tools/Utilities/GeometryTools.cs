using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitFlux.Core.Geometry;

namespace RevitFlux.Tools.Utilities;

/// <summary>Conversions between the shared mm path format and Revit curves.</summary>
internal static class GeometryTools
{
    public const double Mm = 304.8;

    public static double Round(double feet) => Math.Round(feet * Mm, 1);

    /// <summary>Builds curves; <paramref name="map"/> turns mm coordinates into a model point.</summary>
    public static List<Curve> ToCurves(IEnumerable<Segment2D> segments, Func<double, double, XYZ> map)
    {
        var curves = new List<Curve>();
        foreach (var s in segments)
        {
            var a = map(s.X0, s.Y0);
            var b = map(s.X1, s.Y1);
            curves.Add(s.IsArc ? Arc.Create(a, b, map(s.Mx, s.My)) : (Curve)Line.CreateBound(a, b));
        }
        return curves;
    }

    public static CurveLoop ToLoop(JToken? token, string name, Func<double, double, XYZ> map)
    {
        var loop = new CurveLoop();
        foreach (var c in ToCurves(Path2D.Parse(token, true, name), map)) loop.Append(c);
        return loop;
    }

    public static List<Curve> Circle(double x, double y, double radiusMm, Func<double, double, XYZ> map)
    {
        var east = map(x + radiusMm, y);
        var west = map(x - radiusMm, y);
        return new List<Curve>
        {
            Arc.Create(east, west, map(x, y + radiusMm)),
            Arc.Create(west, east, map(x, y - radiusMm))
        };
    }

    /// <summary>Writes curves as path vertices, in order.</summary>
    public static List<double[]> ToPath(IEnumerable<Curve> curves, Func<XYZ, double[]> project, bool closed)
    {
        var path = new List<double[]>();
        XYZ? end = null;
        foreach (var c in curves)
        {
            end = c.GetEndPoint(1);
            if (c is Line)
            {
                path.Add(project(c.GetEndPoint(0)));
            }
            else if (c is Arc)
            {
                var a = project(c.GetEndPoint(0));
                var m = project(c.Evaluate(0.5, true));
                path.Add(new[] { a[0], a[1], m[0], m[1] });
            }
            else
            {
                var points = c.Tessellate();
                for (var i = 0; i < points.Count - 1; i++) path.Add(project(points[i]));
            }
        }
        if (!closed && end != null) path.Add(project(end));
        return path;
    }

    public static double AreaM2(IList<XYZ> points, XYZ u, XYZ v)
    {
        double sum = 0;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            sum += points[j].DotProduct(u) * points[i].DotProduct(v) - points[i].DotProduct(u) * points[j].DotProduct(v);
        }
        return Math.Abs(sum / 2) * 0.09290304;
    }

    public static List<Solid> Solids(Element element, Transform transform)
    {
        var solids = new List<Solid>();
        var stack = new Stack<GeometryElement>();
        var root = element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
        if (root != null) stack.Push(root);
        while (stack.Count > 0)
        {
            foreach (var g in stack.Pop())
            {
                if (g is Solid s && s.Faces.Size > 0 && s.Volume > 1e-9)
                    solids.Add(transform.IsIdentity ? s : SolidUtils.CreateTransformed(s, transform));
                else if (g is GeometryInstance gi)
                {
                    var inner = gi.GetInstanceGeometry();
                    if (inner != null) stack.Push(inner);
                }
            }
        }
        return solids;
    }

    /// <summary>Host-coordinate bounding box in feet, or null.</summary>
    public static BoundingBoxXYZ? HostBox(Element element, Transform transform)
    {
        var bb = element.get_BoundingBox(null);
        if (bb == null) return null;
        var corners = new List<XYZ>();
        foreach (var x in new[] { bb.Min.X, bb.Max.X })
            foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
                foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                    corners.Add(transform.OfPoint(bb.Transform.OfPoint(new XYZ(x, y, z))));
        return new BoundingBoxXYZ
        {
            Min = new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z)),
            Max = new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z))
        };
    }

    public static List<long> Ids(JToken? token) =>
        token is JArray a ? a.Select(t => t.Value<long>()).ToList() : new List<long>();

    /// <summary>Finds a loaded type by id or by "Family: Type", "Type" or family name.</summary>
    public static FamilySymbol FindSymbol(Document doc, JObject input)
    {
        var id = input["typeId"]?.Value<long?>();
        if (id != null)
        {
            return doc.GetElement(ToolHelpers.ToElementId(id.Value)) as FamilySymbol
                ?? throw new ArgumentException($"typeId {id} is not a loadable family type");
        }
        var name = input["type"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("type or typeId is required");
        var hits = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Where(s => Same($"{s.FamilyName}: {s.Name}", name!) || Same(s.Name, name!) || Same(s.FamilyName, name!))
            .ToList();
        if (hits.Count == 1) return hits[0];
        var exact = hits.Where(s => Same($"{s.FamilyName}: {s.Name}", name!)).ToList();
        if (exact.Count == 1) return exact[0];
        if (hits.Count == 0) throw new ArgumentException($"type '{name}' not found");
        throw new ArgumentException($"type '{name}' is ambiguous: " + string.Join("; ",
            hits.Take(6).Select(s => $"{s.FamilyName}: {s.Name} ({ToolHelpers.GetElementIdValue(s.Id)})")));
    }

    public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
