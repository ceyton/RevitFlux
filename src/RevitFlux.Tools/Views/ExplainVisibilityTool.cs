using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitFlux.Core.Results;
using RevitFlux.Core.Session;
using RevitFlux.Core.Tools;
using RevitFlux.Tools.Utilities;

namespace RevitFlux.Tools.Views;

/// <summary>Says whether elements show in a view and lists the likely reasons when they do not.</summary>
[ToolSafety(true, false)]
public class ExplainVisibilityTool : ICortexTool
{
    public string Name => "explain_visibility";
    public string Category => "Views";
    public bool RequiresDocument => true;
    public bool IsDynamic => false;
    public string Description => "Explains why elements are or are not visible in a view.";

    public CortexResult<object> Execute(JObject input, CortexSession session)
    {
        var (doc, error) = ToolHelpers.RequireDocument(session);
        if (error != null) return error;
        var ids = GeometryTools.Ids(input["elementIds"]);
        if (ids.Count == 0) return CortexResult<object>.Fail(CortexErrorCode.InvalidInput, "elementIds is required");
        if (ids.Count > 200) return CortexResult<object>.Fail(CortexErrorCode.InvalidInput, "at most 200 elementIds");

        try
        {
            var viewId = input["viewId"]?.Value<long?>();
            var view = viewId == null ? doc!.ActiveView : doc!.GetElement(ToolHelpers.ToElementId(viewId.Value)) as View;
            if (view == null || view.IsTemplate || !view.AreGraphicsOverridesAllowed())
                return CortexResult<object>.Fail(CortexErrorCode.InvalidInput, "viewId must be a graphical view");

            var visible = new HashSet<ElementId>(new FilteredElementCollector(doc, view.Id).ToElementIds());
            var results = new List<object>();
            foreach (var id in ids)
            {
                var e = doc!.GetElement(ToolHelpers.ToElementId(id));
                if (e == null) { results.Add(new { id, inView = false, reasons = new List<string> { "element not found" } }); continue; }
                var reasons = Safe(() => Reasons(doc, view, e));
                var inView = visible.Contains(e.Id);
                if (inView) reasons.RemoveAll(x => x.StartsWith("starts above", StringComparison.Ordinal));
                if (!inView && reasons.Count == 0) reasons.Add("no cause found; it may be covered, outside the far clip, or too small at this scale");
                results.Add(new { id, inView, reasons });
            }
            return CortexResult<object>.Ok(new
            {
                viewName = view.Name,
                template = view.ViewTemplateId == ElementId.InvalidElementId ? null : doc!.GetElement(view.ViewTemplateId)?.Name,
                note = "inView means Revit collects it for the view; it can still be covered by other elements",
                results
            });
        }
        catch (Exception ex)
        {
            return CortexResult<object>.Fail(CortexErrorCode.Unknown, $"explain_visibility failed: {ex.Message}");
        }
    }

    private static List<string> Safe(Func<List<string>> f)
    {
        try { return f(); }
        catch (Exception ex) { return new List<string> { $"check failed: {ex.Message}" }; }
    }

    private static List<string> Reasons(Document doc, View view, Element e)
    {
        var r = new List<string>();
        if (e.ViewSpecific && e.OwnerViewId != view.Id) r.Add($"belongs to view '{doc.GetElement(e.OwnerViewId)?.Name}'");
        if (e.IsHidden(view)) r.Add("hidden in this view (element)");
        if (view.IsTemporaryHideIsolateActive()) r.Add("temporary hide/isolate is active in this view");

        var category = e.Category;
        if (category != null)
        {
            if (view.CanCategoryBeHidden(category.Id) && view.GetCategoryHidden(category.Id)) r.Add($"category '{category.Name}' is off");
            if (category.CategoryType == CategoryType.Model && view.AreModelCategoriesHidden) r.Add("all model categories are off");
            if (category.CategoryType == CategoryType.Annotation && view.AreAnnotationCategoriesHidden) r.Add("all annotation categories are off");
        }

        foreach (var filterId in view.GetFilters())
        {
            if (view.GetFilterVisibility(filterId)) continue;
            if (doc.GetElement(filterId) is not ParameterFilterElement f) continue;
            if (category == null || !f.GetCategories().Contains(category.Id)) continue;
            var rule = f.GetElementFilter();
            if (rule == null || rule.PassesFilter(e)) r.Add($"view filter '{f.Name}' hides it");
        }

        if (doc.IsWorkshared && e.WorksetId != WorksetId.InvalidWorksetId)
        {
            var workset = doc.GetWorksetTable().GetWorkset(e.WorksetId);
            if (workset != null && workset.Kind == WorksetKind.UserWorkset)
            {
                if (!workset.IsOpen) r.Add($"workset '{workset.Name}' is closed");
                if (!view.IsWorksetVisible(workset.Id)) r.Add($"workset '{workset.Name}' is not visible in this view");
            }
        }

        var phase = view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
        if (phase != null && phase != ElementId.InvalidElementId && e.HasPhases())
        {
            var status = e.GetPhaseStatus(phase);
            var filterId = view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId();
            var filter = filterId == null ? null : doc.GetElement(filterId) as PhaseFilter;
            if (status == ElementOnPhaseStatus.Past || status == ElementOnPhaseStatus.Future)
                r.Add($"phase: {status.ToString().ToLowerInvariant()} in view phase '{doc.GetElement(phase)?.Name}'");
            else if (filter != null && status != ElementOnPhaseStatus.None
                     && filter.GetPhaseStatusPresentation(status) == PhaseStatusPresentation.DontShow)
                r.Add($"phase filter '{filter.Name}' hides {status.ToString().ToLowerInvariant()} elements");
        }

        if (e.DesignOption != null && !e.DesignOption.IsPrimary) r.Add($"in secondary design option '{e.DesignOption.Name}'");

        var box = e.get_BoundingBox(null);
        if (box == null)
        {
            if (!e.ViewSpecific) r.Add("has no model geometry");
            return r;
        }

        if (view is View3D v3 && v3.IsSectionBoxActive)
        {
            var sb = v3.GetSectionBox();
            if (Outside(box, sb.Transform.Inverse, sb.Min, sb.Max, true)) r.Add("outside the section box");
        }
        else if (view.CropBoxActive && view is not View3D)
        {
            var crop = view.CropBox;
            if (Outside(box, crop.Transform.Inverse, crop.Min, crop.Max, view is ViewSection)) r.Add("outside the crop region");
        }

        if (view is ViewPlan plan && plan.GenLevel != null)
        {
            var range = plan.GetViewRange();
            var top = Elevation(doc, range, PlanViewPlane.TopClipPlane);
            var cut = Elevation(doc, range, PlanViewPlane.CutPlane);
            var depth = Elevation(doc, range, PlanViewPlane.ViewDepthPlane);
            var ceiling = plan.ViewType == ViewType.CeilingPlan;
            var hi = ceiling ? Max(top, depth) : top;
            var lo = ceiling ? cut : depth;
            if (hi != null && box.Min.Z > hi.Value) r.Add($"above the view range (bottom {GeometryTools.Round(box.Min.Z)}, limit {GeometryTools.Round(hi.Value)} mm)");
            if (!ceiling && cut != null && box.Min.Z > cut.Value && (hi == null || box.Min.Z <= hi.Value))
                r.Add($"starts above the cut plane ({GeometryTools.Round(cut.Value)} mm), so most categories do not show in plan");
            if (lo != null && box.Max.Z < lo.Value) r.Add($"below the view range (top {GeometryTools.Round(box.Max.Z)}, limit {GeometryTools.Round(lo.Value)} mm)");
        }
        return r;
    }

    private static double? Max(double? a, double? b) => a == null || b == null ? null : Math.Max(a.Value, b.Value);

    private static double? Elevation(Document doc, PlanViewRange range, PlanViewPlane plane)
    {
        // Unlimited and level-above/below references are not levels; skip the check then.
        return doc.GetElement(range.GetLevelId(plane)) is Level level
            ? level.ProjectElevation + range.GetOffset(plane)
            : (double?)null;
    }

    private static bool Outside(BoundingBoxXYZ box, Transform toLocal, XYZ min, XYZ max, bool checkDepth)
    {
        var pts = new List<XYZ>();
        foreach (var x in new[] { box.Min.X, box.Max.X })
            foreach (var y in new[] { box.Min.Y, box.Max.Y })
                foreach (var z in new[] { box.Min.Z, box.Max.Z })
                    pts.Add(toLocal.OfPoint(box.Transform.OfPoint(new XYZ(x, y, z))));
        return pts.Max(p => p.X) < min.X || pts.Min(p => p.X) > max.X
            || pts.Max(p => p.Y) < min.Y || pts.Min(p => p.Y) > max.Y
            || (checkDepth && (pts.Max(p => p.Z) < min.Z || pts.Min(p => p.Z) > max.Z));
    }
}
