using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitFlux.Core.Results;
using RevitFlux.Core.Session;
using RevitFlux.Core.Tools;
using RevitFlux.Tools.Utilities;

namespace RevitFlux.Tools.Elements;

/// <summary>Creates many filled regions in one view, in one transaction (one undo step).</summary>
[ToolSafety(false, false)]
public class CreateFilledRegionsTool : ICortexTool
{
    public string Name => "create_filled_regions";
    public string Category => "Elements";
    public bool RequiresDocument => true;
    public bool IsDynamic => false;
    public string Description => "Creates many filled regions in one view in a single transaction. All-or-nothing unless continueOnError is true.";
    private const double MmPerFoot = 304.8;

    public CortexResult<object> Execute(JObject input, CortexSession session)
    {
        var doc = session.Store.Get<object>("activeDocument") as Document;
        if (doc == null)
            return CortexResult<object>.Fail(CortexErrorCode.InvalidInput, "No active document in session");

        var regions = input["regions"] as JArray;
        if (regions == null || regions.Count == 0)
            return CortexResult<object>.Fail(CortexErrorCode.InvalidInput, "regions must be a non-empty array");

        var continueOnError = input["continueOnError"]?.Value<bool>() ?? false;
        var dryRun = input["dryRun"]?.Value<bool>() ?? false;
        var defaultType = input["filledRegionTypeName"]?.Value<string>();
        var viewId = input["viewId"]?.Value<long>() ?? -1;

        try
        {
            View? view;
            if (viewId > 0)
            {
#if REVIT2024_OR_GREATER
                view = doc.GetElement(new ElementId(viewId)) as View;
#else
                view = doc.GetElement(new ElementId((int)viewId)) as View;
#endif
            }
            else
            {
                view = doc.ActiveView;
            }
            if (view == null)
                return CortexResult<object>.Fail(CortexErrorCode.InvalidInput, "Could not resolve view");

            var types = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType))
                .Cast<FilledRegionType>().ToList();
            if (types.Count == 0)
                return CortexResult<object>.Fail(CortexErrorCode.ElementNotFound, "No filled region types available");

            var errors = new List<object>();
            var prepared = new List<PreparedRegion>();
            for (var i = 0; i < regions.Count; i++)
            {
                try
                {
                    var item = regions[i] as JObject ?? throw new ArgumentException("region must be an object");
                    var typeName = item["filledRegionTypeName"]?.Value<string>() ?? defaultType;
                    var type = string.IsNullOrEmpty(typeName)
                        ? types[0]
                        : types.FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                          ?? throw new ArgumentException($"filled region type '{typeName}' not found");
                    var loops = new List<CurveLoop> { BuildLoop(item["boundaryPoints"], "boundaryPoints") };
                    if (item["holes"] is JArray holes)
                    {
                        for (var h = 0; h < holes.Count; h++)
                            loops.Add(BuildLoop(holes[h], $"holes[{h}]"));
                    }
                    prepared.Add(new PreparedRegion(i, type, loops));
                }
                catch (Exception ex)
                {
                    errors.Add(new { index = i, error = ex.Message });
                }
            }

            if (dryRun)
                return CortexResult<object>.Ok(new { dryRun = true, valid = prepared.Count, viewName = view.Name, errors });

            if (errors.Count > 0 && !continueOnError)
            {
                return CortexResult<object>.Fail(CortexErrorCode.InvalidInput,
                    $"{errors.Count} of {regions.Count} regions are invalid; nothing was created: " +
                    JArray.FromObject(errors.Take(10)).ToString(Newtonsoft.Json.Formatting.None),
                    suggestion: "Fix the listed regions, or set continueOnError=true to skip them.");
            }

            var created = new List<object>();
            if (prepared.Count > 0)
            {
                using var tx = new Transaction(doc, "RevitFlux: Create Filled Regions");
                var txFailures = TransactionFailureHandling.SuppressWarnings(tx);
                tx.Start();
                foreach (var p in prepared)
                {
                    try
                    {
                        var region = FilledRegion.Create(doc, p.Type.Id, view.Id, p.Loops);
                        created.Add(new { index = p.Index, filledRegionId = ToolHelpers.GetElementIdValue(region.Id) });
                    }
                    catch (Exception ex)
                    {
                        if (!continueOnError)
                        {
                            tx.RollBack();
                            return CortexResult<object>.Fail(CortexErrorCode.InvalidInput,
                                $"Region {p.Index} failed; nothing was created: {ex.Message}");
                        }
                        errors.Add(new { index = p.Index, error = ex.Message });
                    }
                }
                if (tx.Commit() != TransactionStatus.Committed)
                {
                    return CortexResult<object>.Fail(CortexErrorCode.TransactionFailed,
                        $"Revit rolled back the transaction: {TransactionFailureHandling.Describe(txFailures)}",
                        suggestion: "Fix the reported model errors and retry.");
                }
            }

            return CortexResult<object>.Ok(new { createdCount = created.Count, created, viewName = view.Name, errors });
        }
        catch (Exception ex)
        {
            return CortexResult<object>.Fail(CortexErrorCode.Unknown, $"Failed to create filled regions: {ex.Message}");
        }
    }

    private static CurveLoop BuildLoop(JToken? token, string name)
    {
        var points = token as JArray;
        if (points == null || points.Count < 3)
            throw new ArgumentException($"{name} needs at least 3 points");
        var xyz = points.Select(p => new XYZ(Coordinate(p, "x", name), Coordinate(p, "y", name), 0)).ToList();
        var loop = new CurveLoop();
        for (var i = 0; i < xyz.Count; i++)
            loop.Append(Line.CreateBound(xyz[i], xyz[(i + 1) % xyz.Count]));
        return loop;
    }

    private static double Coordinate(JToken point, string axis, string name)
    {
        var value = (point as JObject)?[axis]?.Value<double?>()
            ?? throw new ArgumentException($"{name}: every point needs numeric x and y");
        return value / MmPerFoot;
    }

    private sealed class PreparedRegion
    {
        public PreparedRegion(int index, FilledRegionType type, List<CurveLoop> loops)
        {
            Index = index;
            Type = type;
            Loops = loops;
        }

        public int Index { get; }
        public FilledRegionType Type { get; }
        public List<CurveLoop> Loops { get; }
    }
}
