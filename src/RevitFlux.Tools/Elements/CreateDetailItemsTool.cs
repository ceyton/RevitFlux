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

/// <summary>Draws many regions, lines, circles, notes and detail symbols in one undo step.</summary>
[ToolSafety(false, false)]
public class CreateDetailItemsTool : ICortexTool
{
    public string Name => "create_detail_items";
    public string Category => "Elements";
    public bool RequiresDocument => true;
    public bool IsDynamic => false;
    public string Description => "Draws many filled regions, lines, circles, text notes and detail symbols in one view in a single transaction.";

    public CortexResult<object> Execute(JObject input, CortexSession session)
    {
        var (doc, error) = ToolHelpers.RequireDocument(session);
        if (error != null) return error;
        if (input["items"] is not JArray items || items.Count == 0)
            return CortexResult<object>.Fail(CortexErrorCode.InvalidInput, "items must be a non-empty array");

        try
        {
            var viewId = input["viewId"]?.Value<long?>();
            var view = viewId == null ? doc!.ActiveView : doc!.GetElement(ToolHelpers.ToElementId(viewId.Value)) as View;
            if (view == null || view.IsTemplate || view is View3D || view is ViewSchedule)
                return CortexResult<object>.Fail(CortexErrorCode.InvalidInput, "viewId must be a plan, section, elevation, drafting or sheet view");

            var map = Mapper(view);
            var actions = new List<Func<IEnumerable<Element>>>();
            var errors = new List<object>();
            for (var i = 0; i < items.Count; i++)
            {
                try
                {
                    actions.Add(Prepare(doc!, view, items[i] as JObject ?? throw new ArgumentException("item must be an object"), map));
                }
                catch (Exception ex)
                {
                    errors.Add(new { index = i, error = ex.Message });
                }
            }
            if (errors.Count > 0)
            {
                return CortexResult<object>.Fail(CortexErrorCode.InvalidInput,
                    $"{errors.Count} of {items.Count} items are invalid; nothing was created: " +
                    JArray.FromObject(errors.Take(10)).ToString(Newtonsoft.Json.Formatting.None));
            }
            if (ToolHelpers.GetDryRun(input, false))
                return CortexResult<object>.Ok(new { dryRun = true, valid = items.Count, viewName = view.Name });

            var comment = input["comment"]?.Value<string>();
            var ids = new List<List<long>>();
            using var tx = new Transaction(doc, "RevitFlux: Create Detail Items");
            var failures = TransactionFailureHandling.SuppressWarnings(tx);
            tx.Start();
            for (var i = 0; i < actions.Count; i++)
            {
                try
                {
                    var created = actions[i]().ToList();
                    if (!string.IsNullOrEmpty(comment))
                    {
                        foreach (var e in created)
                        {
                            var p = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                            if (p != null && !p.IsReadOnly) p.Set(comment);
                        }
                    }
                    ids.Add(created.Select(e => ToolHelpers.GetElementIdValue(e.Id)).ToList());
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    return CortexResult<object>.Fail(CortexErrorCode.InvalidInput,
                        $"Item {i} failed; nothing was created: {ex.Message}");
                }
            }
            if (tx.Commit() != TransactionStatus.Committed)
            {
                return CortexResult<object>.Fail(CortexErrorCode.TransactionFailed,
                    $"Revit rolled back: {TransactionFailureHandling.Describe(failures)}");
            }
            return CortexResult<object>.Ok(new { itemCount = ids.Count, viewName = view.Name, idsPerItem = ids });
        }
        catch (Exception ex)
        {
            return CortexResult<object>.Fail(CortexErrorCode.Unknown, $"create_detail_items failed: {ex.Message}");
        }
    }

    /// <summary>Plans use model X/Y; every other view uses its own right/up axes from its origin.</summary>
    private static Func<double, double, XYZ> Mapper(View view)
    {
        if (view is ViewPlan plan)
        {
            var z = plan.GenLevel?.ProjectElevation ?? view.Origin.Z;
            return (x, y) => new XYZ(x / GeometryTools.Mm, y / GeometryTools.Mm, z);
        }
        if (view is ViewDrafting || view is ViewSheet)
            return (x, y) => new XYZ(x / GeometryTools.Mm, y / GeometryTools.Mm, 0);
        var o = view.Origin;
        var r = view.RightDirection;
        var u = view.UpDirection;
        return (x, y) => o + r * (x / GeometryTools.Mm) + u * (y / GeometryTools.Mm);
    }

    private static Func<IEnumerable<Element>> Prepare(Document doc, View view, JObject item, Func<double, double, XYZ> map)
    {
        var style = item["style"]?.Value<string>();
        switch (item["kind"]?.Value<string>())
        {
            case "region":
            {
                var type = FindType<FilledRegionType>(doc, style);
                var loops = (item["loops"] as JArray ?? throw new ArgumentException("loops is required"))
                    .Select((l, n) => GeometryTools.ToLoop(l, $"loops[{n}]", map)).ToList();
                if (loops.Count == 0) throw new ArgumentException("loops is empty");
                return () => new Element[] { FilledRegion.Create(doc, type.Id, view.Id, loops) };
            }
            case "line":
            {
                var closed = item["closed"]?.Value<bool>() ?? false;
                var curves = GeometryTools.ToCurves(Path2D.Parse(item["path"], closed, "path"), map);
                return () => Draw(doc, view, curves, LineStyle(doc, style));
            }
            case "circle":
            {
                var at = Path2D.Numbers(item["at"], "at");
                var radius = item["radius"]?.Value<double?>() ?? 0;
                if (radius <= 0) throw new ArgumentException("radius must be positive");
                var curves = GeometryTools.Circle(at[0], at[1], radius, map);
                return () => Draw(doc, view, curves, LineStyle(doc, style));
            }
            case "text":
            {
                var text = item["text"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text is required");
                var at = Path2D.Numbers(item["at"], "at");
                var type = FindType<TextNoteType>(doc, style);
                var leaders = (item["leaders"] as JArray ?? new JArray()).Select(l => Path2D.Numbers(l, "leaders")).ToList();
                var width = item["width"]?.Value<double?>();
                var options = new TextNoteOptions(type.Id);
                return () =>
                {
                    var p = map(at[0], at[1]);
                    var note = width == null
                        ? TextNote.Create(doc, view.Id, p, text, options)
                        : TextNote.Create(doc, view.Id, p, Math.Max(TextNote.GetMinimumAllowedWidth(doc, type.Id),
                            Math.Min(TextNote.GetMaximumAllowedWidth(doc, type.Id), width.Value / GeometryTools.Mm)), text, options);
                    foreach (var l in leaders)
                    {
                        var left = (map(l[0], l[1]) - p).DotProduct(view.RightDirection) < 0;
                        note.AddLeader(left ? TextNoteLeaderTypes.TNLT_STRAIGHT_L : TextNoteLeaderTypes.TNLT_STRAIGHT_R)
                            .End = map(l[0], l[1]);
                    }
                    return new Element[] { note };
                };
            }
            case "symbol":
            {
                var symbol = GeometryTools.FindSymbol(doc, item);
                var at = Path2D.Numbers(item["at"], "at");
                var rotation = item["rotation"]?.Value<double?>() ?? 0;
                return () =>
                {
                    if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                    var p = map(at[0], at[1]);
                    var instance = doc.Create.NewFamilyInstance(p, symbol, view);
                    if (Math.Abs(rotation) > 1e-9)
                        ElementTransformUtils.RotateElement(doc, instance.Id,
                            Line.CreateBound(p, p + view.ViewDirection), rotation * Math.PI / 180);
                    return new Element[] { instance };
                };
            }
            default:
                throw new ArgumentException("kind must be region, line, circle, text or symbol");
        }
    }

    private static IEnumerable<Element> Draw(Document doc, View view, List<Curve> curves, GraphicsStyle? style)
    {
        foreach (var c in curves)
        {
            var d = doc.Create.NewDetailCurve(view, c);
            if (style != null) d.LineStyle = style;
            yield return d;
        }
    }

    private static T FindType<T>(Document doc, string? name) where T : ElementType
    {
        var types = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().ToList();
        if (string.IsNullOrEmpty(name))
            return types.FirstOrDefault() ?? throw new ArgumentException($"no {typeof(T).Name} in the model");
        return types.FirstOrDefault(t => GeometryTools.Same(t.Name, name!))
            ?? throw new ArgumentException($"style '{name}' not found; available: {string.Join(", ", types.Take(15).Select(t => t.Name))}");
    }

    private static GraphicsStyle? LineStyle(Document doc, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines).SubCategories.Cast<Category>().ToList();
        var hit = lines.FirstOrDefault(c => GeometryTools.Same(c.Name, name!))
            ?? throw new ArgumentException($"line style '{name}' not found; available: {string.Join(", ", lines.Take(15).Select(c => c.Name))}");
        return hit.GetGraphicsStyle(GraphicsStyleType.Projection);
    }
}
