using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitFlux.Core.Geometry;
using RevitFlux.Core.Results;
using RevitFlux.Core.Session;
using RevitFlux.Core.Tools;
using RevitFlux.Tools.Utilities;

namespace RevitFlux.Tools.Elements;

/// <summary>Reads positions, horizontal face outlines, or section cuts of host or linked elements.</summary>
[ToolSafety(true, false)]
public class GetGeometryTool : ICortexTool
{
    public string Name => "get_geometry";
    public string Category => "Elements";
    public bool RequiresDocument => true;
    public bool IsDynamic => false;
    public string Description => "Reads positions, face outlines or section cuts of host or linked elements, in host mm.";

    public CortexResult<object> Execute(JObject input, CortexSession session)
    {
        var (doc, error) = ToolHelpers.RequireDocument(session);
        if (error != null) return error;

        try
        {
            var mode = input["mode"]?.Value<string>() ?? "location";
            if (mode != "location" && mode != "faces" && mode != "slice")
                return Invalid("mode must be location, faces or slice");

            var source = doc!;
            var transform = Transform.Identity;
            var linkId = input["linkId"]?.Value<long?>();
            if (linkId != null)
            {
                var link = doc!.GetElement(ToolHelpers.ToElementId(linkId.Value)) as RevitLinkInstance;
                if (link == null) return Invalid($"linkId {linkId} is not a link instance");
                source = link.GetLinkDocument();
                if (source == null) return Invalid($"link '{link.Name}' is not loaded");
                transform = link.GetTotalTransform();
            }

            var missing = new List<long>();
            var elements = Collect(source, input, missing, out var problem);
            if (problem != null) return Invalid(problem);

            var region = input["region"] as JArray;
            if (region != null)
            {
                var r = Path2D.Numbers(region, "region", 4).Select(v => v / GeometryTools.Mm).ToArray();
                if (r.Length != 4 && r.Length != 6) return Invalid("region is [minX,minY,maxX,maxY] or [minX,minY,minZ,maxX,maxY,maxZ]");
                var flat = r.Length == 4;
                elements = elements.Where(e =>
                {
                    var b = GeometryTools.HostBox(e, transform);
                    if (b == null) return false;
                    return flat
                        ? b.Max.X >= r[0] && b.Max.Y >= r[1] && b.Min.X <= r[2] && b.Min.Y <= r[3]
                        : b.Max.X >= r[0] && b.Max.Y >= r[1] && b.Max.Z >= r[2]
                          && b.Min.X <= r[3] && b.Min.Y <= r[4] && b.Min.Z <= r[5];
                }).ToList();
            }

            var total = elements.Count;
            var max = Math.Max(1, input["maxElements"]?.Value<int?>() ?? 100);
            elements = elements.Take(max).ToList();

            object body;
            var skipped = 0;
            if (mode == "location")
                body = elements.Select(e => Location(source, e, transform)).ToList();
            else if (mode == "faces")
                body = Faces(elements, transform, input);
            else
                body = Slices(elements, transform, input, ref skipped);

            return CortexResult<object>.Ok(new
            {
                totalCount = total,
                elementCount = elements.Count,
                truncated = elements.Count < total,
                missing,
                uncuttableSolids = skipped,
                items = body
            });
        }
        catch (ArgumentException ex)
        {
            return Invalid(ex.Message);
        }
        catch (Exception ex)
        {
            return CortexResult<object>.Fail(CortexErrorCode.Unknown, $"get_geometry failed: {ex.Message}");
        }
    }

    private static CortexResult<object> Invalid(string message) =>
        CortexResult<object>.Fail(CortexErrorCode.InvalidInput, message);

    private static List<Element> Collect(Document source, JObject input, List<long> missing, out string? problem)
    {
        problem = null;
        var ids = GeometryTools.Ids(input["elementIds"]);
        if (ids.Count > 0)
        {
            var found = new List<Element>();
            foreach (var id in ids)
            {
                var e = source.GetElement(ToolHelpers.ToElementId(id));
                if (e == null) missing.Add(id); else found.Add(e);
            }
            return found;
        }

        var categories = input["categories"]?.ToObject<List<string>>() ?? new List<string>();
        if (categories.Count == 0)
        {
            problem = "elementIds or categories is required";
            return new List<Element>();
        }
        var result = new List<Element>();
        foreach (var name in categories)
        {
            var id = CategoryResolver.ResolveToId(source, name);
            if (id == null || id == ElementId.InvalidElementId)
            {
                problem = $"category '{name}' not found";
                return result;
            }
            result.AddRange(new FilteredElementCollector(source).OfCategoryId(id).WhereElementIsNotElementType());
        }
        var typeFilter = input["typeContains"]?.Value<string>();
        if (!string.IsNullOrEmpty(typeFilter))
            result = result.Where(e => TypeName(source, e).IndexOf(typeFilter!, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        return result;
    }

    private static string TypeName(Document source, Element e) =>
        source.GetElement(e.GetTypeId()) is ElementType t ? $"{t.FamilyName}: {t.Name}" : e.Name;

    private static double[] Xyz(XYZ p) => new[] { GeometryTools.Round(p.X), GeometryTools.Round(p.Y), GeometryTools.Round(p.Z) };

    private static object Location(Document source, Element e, Transform t)
    {
        object? at = null;
        double? rotation = null;
        if (e.Location is LocationPoint lp)
        {
            at = Xyz(t.OfPoint(lp.Point));
            try { rotation = Math.Round(lp.Rotation * 180 / Math.PI, 3); }
            catch (Exception) { /* some points have no rotation */ }
        }
        else if (e.Location is LocationCurve lc)
        {
            var c = lc.Curve.CreateTransformed(t);
            at = c.IsBound
                ? c is Line
                    ? new[] { Xyz(c.GetEndPoint(0)), Xyz(c.GetEndPoint(1)) }
                    : new[] { Xyz(c.GetEndPoint(0)), Xyz(c.Evaluate(0.5, true)), Xyz(c.GetEndPoint(1)) }
                : null;
        }
        var b = GeometryTools.HostBox(e, t);
        return new
        {
            id = ToolHelpers.GetElementIdValue(e.Id),
            type = TypeName(source, e),
            mark = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
            at,
            rotation,
            box = b == null ? null : new[] { Xyz(b.Min), Xyz(b.Max) }
        };
    }

    private static List<object> Faces(List<Element> elements, Transform t, JObject input)
    {
        var up = (input["side"]?.Value<string>() ?? "bottom") == "top";
        var min = input["minAreaM2"]?.Value<double?>() ?? 0;
        var max = input["maxAreaM2"]?.Value<double?>() ?? double.MaxValue;
        var items = new List<object>();
        foreach (var e in elements)
        {
            foreach (var face in GeometryTools.Solids(e, t).SelectMany(s => s.Faces.Cast<Face>()).OfType<PlanarFace>())
            {
                if (up ? face.FaceNormal.Z < 0.999 : face.FaceNormal.Z > -0.999) continue;
                AddLoops(items, e, face, XYZ.BasisX, XYZ.BasisY, XYZ.Zero, min, max, GeometryTools.Round(face.Origin.Z));
            }
        }
        return items;
    }

    private static List<object> Slices(List<Element> elements, Transform t, JObject input, ref int skipped)
    {
        XYZ origin, normal, u, v;
        double? z = input["z"]?.Value<double?>();
        if (z != null)
        {
            origin = new XYZ(0, 0, z.Value / GeometryTools.Mm);
            normal = XYZ.BasisZ; u = XYZ.BasisX; v = XYZ.BasisY;
        }
        else if (input["line"] is JArray line && line.Count == 2)
        {
            var a = Path2D.Numbers(line[0], "line");
            var b = Path2D.Numbers(line[1], "line");
            origin = new XYZ(a[0] / GeometryTools.Mm, a[1] / GeometryTools.Mm, 0);
            var end = new XYZ(b[0] / GeometryTools.Mm, b[1] / GeometryTools.Mm, 0);
            if (origin.DistanceTo(end) < 0.01) throw new ArgumentException("line: points are too close");
            u = (end - origin).Normalize(); v = XYZ.BasisZ; normal = u.CrossProduct(v);
        }
        else
        {
            throw new ArgumentException("slice needs z (mm) or line [[x,y],[x,y]]");
        }

        var min = input["minAreaM2"]?.Value<double?>() ?? 0;
        var max = input["maxAreaM2"]?.Value<double?>() ?? double.MaxValue;
        var plane = Plane.CreateByNormalAndOrigin(normal, origin);
        var items = new List<object>();
        foreach (var e in elements)
        {
            foreach (var solid in GeometryTools.Solids(e, t))
            {
                Solid? cut;
                try { cut = BooleanOperationsUtils.CutWithHalfSpace(solid, plane); }
                catch (Exception) { skipped++; continue; }
                if (cut == null) continue;
                foreach (var face in cut.Faces.Cast<Face>().OfType<PlanarFace>())
                {
                    // The cut face looks away from the kept half.
                    if (face.FaceNormal.DotProduct(normal) > -0.9999) continue;
                    if (Math.Abs((face.Origin - origin).DotProduct(normal)) > 1e-4) continue;
                    AddLoops(items, e, face, u, v, z != null ? XYZ.Zero : origin, min, max, null);
                }
            }
        }
        return items;
    }

    private static void AddLoops(List<object> items, Element e, PlanarFace face, XYZ u, XYZ v, XYZ origin,
        double min, double max, double? z)
    {
        foreach (var loop in face.GetEdgesAsCurveLoops())
        {
            var area = GeometryTools.AreaM2(loop, u, v);
            if (area < min || area > max) continue;
            items.Add(new
            {
                id = ToolHelpers.GetElementIdValue(e.Id),
                z,
                areaM2 = Math.Round(area, 4),
                path = GeometryTools.ToPath(loop,
                    p => new[] { GeometryTools.Round((p - origin).DotProduct(u)), GeometryTools.Round((p - origin).DotProduct(v)) }, true)
            });
        }
    }
}
