using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using RevitFlux.Core.Geometry;
using RevitFlux.Core.Results;
using RevitFlux.Core.Session;
using RevitFlux.Core.Tools;
using RevitFlux.Tools.Utilities;

namespace RevitFlux.Tools.Elements;

/// <summary>Places many instances of one type, by point or by line/arc, in one undo step.</summary>
[ToolSafety(false, false)]
public class PlaceInstancesTool : ICortexTool
{
    public string Name => "place_instances";
    public string Category => "Elements";
    public bool RequiresDocument => true;
    public bool IsDynamic => false;
    public string Description => "Places many instances of a family type by point, or beams by line or arc, in a single transaction.";

    private sealed class Placement
    {
        public XYZ? Point;
        public Curve? Curve;
        public double Rotation;
        public Level Level = null!;
        public Element? Host;
        public bool FlipFacing;
        public bool FlipHand;
    }

    public CortexResult<object> Execute(JObject input, CortexSession session)
    {
        var (doc, error) = ToolHelpers.RequireDocument(session);
        if (error != null) return error;
        if (input["placements"] is not JArray array || array.Count == 0)
            return CortexResult<object>.Fail(CortexErrorCode.InvalidInput, "placements must be a non-empty array");

        try
        {
            var symbol = GeometryTools.FindSymbol(doc!, input);
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.ProjectElevation).ToList();
            if (levels.Count == 0) return CortexResult<object>.Fail(CortexErrorCode.InvalidInput, "the model has no levels");

            var placements = new List<Placement>();
            for (var i = 0; i < array.Count; i++)
            {
                try
                {
                    placements.Add(Parse(doc!, array[i] as JObject ?? throw new ArgumentException("must be an object"), levels));
                }
                catch (Exception ex)
                {
                    return CortexResult<object>.Fail(CortexErrorCode.InvalidInput, $"placements[{i}]: {ex.Message}; nothing was created");
                }
            }
            if (ToolHelpers.GetDryRun(input, false))
                return CortexResult<object>.Ok(new { dryRun = true, valid = placements.Count, type = $"{symbol.FamilyName}: {symbol.Name}" });

            var comment = input["comment"]?.Value<string>();
            var created = new List<FamilyInstance>();
            double maxOffset = 0;
            using var tx = new Transaction(doc, "RevitFlux: Place Instances");
            var failures = TransactionFailureHandling.SuppressWarnings(tx);
            tx.Start();
            try
            {
                if (!symbol.IsActive) { symbol.Activate(); doc!.Regenerate(); }
                foreach (var p in placements) created.Add(Create(doc!, symbol, p));
                doc!.Regenerate();

                for (var i = 0; i < placements.Count; i++)
                {
                    var p = placements[i];
                    var e = created[i];
                    if (p.Point != null && e.Location is LocationPoint at)
                    {
                        // Revit reads Z as a level offset for some families, so correct the result.
                        // Hosted instances are positioned by their host.
                        var delta = p.Point - at.Point;
                        if (p.Host == null && delta.GetLength() > 1e-6)
                            ElementTransformUtils.MoveElement(doc, e.Id, delta);
                        if (Math.Abs(p.Rotation) > 1e-9)
                            ElementTransformUtils.RotateElement(doc, e.Id,
                                Line.CreateBound(p.Point, p.Point + XYZ.BasisZ), p.Rotation * Math.PI / 180);
                    }
                    if (p.FlipFacing && e.CanFlipFacing) e.flipFacing();
                    if (p.FlipHand && e.CanFlipHand) e.flipHand();
                    if (!string.IsNullOrEmpty(comment))
                    {
                        var c = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                        if (c != null && !c.IsReadOnly) c.Set(comment);
                    }
                }
                doc.Regenerate();
                for (var i = 0; i < placements.Count; i++)
                {
                    if (placements[i].Point != null && created[i].Location is LocationPoint at)
                        maxOffset = Math.Max(maxOffset, at.Point.DistanceTo(placements[i].Point));
                }
            }
            catch (Exception ex)
            {
                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                return CortexResult<object>.Fail(CortexErrorCode.InvalidInput,
                    $"Placement {created.Count} or a later step failed; nothing was created: {ex.Message}");
            }
            if (tx.Commit() != TransactionStatus.Committed)
            {
                return CortexResult<object>.Fail(CortexErrorCode.TransactionFailed,
                    $"Revit rolled back: {TransactionFailureHandling.Describe(failures)}");
            }
            return CortexResult<object>.Ok(new
            {
                createdCount = created.Count,
                type = $"{symbol.FamilyName}: {symbol.Name}",
                maxPointErrorMm = Math.Round(maxOffset * GeometryTools.Mm, 2),
                ids = created.Select(e => ToolHelpers.GetElementIdValue(e.Id)).ToList()
            });
        }
        catch (ArgumentException ex)
        {
            return CortexResult<object>.Fail(CortexErrorCode.InvalidInput, ex.Message);
        }
        catch (Exception ex)
        {
            return CortexResult<object>.Fail(CortexErrorCode.Unknown, $"place_instances failed: {ex.Message}");
        }
    }

    private static Placement Parse(Document doc, JObject item, List<Level> levels)
    {
        var p = new Placement
        {
            Rotation = item["rotation"]?.Value<double?>() ?? 0,
            FlipFacing = item["flipFacing"]?.Value<bool>() ?? false,
            FlipHand = item["flipHand"]?.Value<bool>() ?? false
        };
        if (item["at"] != null)
        {
            p.Point = Point(item["at"], "at");
        }
        else if (item["line"] is JArray line && (line.Count == 2 || line.Count == 3))
        {
            var a = Point(line[0], "line");
            var b = Point(line[line.Count - 1], "line");
            p.Curve = line.Count == 2 ? Line.CreateBound(a, b) : (Curve)Arc.Create(a, b, Point(line[1], "line"));
        }
        else
        {
            throw new ArgumentException("needs at [x,y,z] or line [[x,y,z],[x,y,z]] (3 points = arc through the middle one)");
        }

        var levelId = item["levelId"]?.Value<long?>();
        var z = (p.Point ?? p.Curve!.GetEndPoint(0)).Z;
        p.Level = levelId != null
            ? doc.GetElement(ToolHelpers.ToElementId(levelId.Value)) as Level ?? throw new ArgumentException($"levelId {levelId} is not a level")
            : levels.LastOrDefault(l => l.ProjectElevation <= z + 0.003) ?? levels[0];

        var hostId = item["hostId"]?.Value<long?>();
        if (hostId != null)
            p.Host = doc.GetElement(ToolHelpers.ToElementId(hostId.Value)) ?? throw new ArgumentException($"hostId {hostId} not found");
        return p;
    }

    private static XYZ Point(JToken? token, string name)
    {
        var n = Path2D.Numbers(token, name, 3);
        return new XYZ(n[0] / GeometryTools.Mm, n[1] / GeometryTools.Mm, n[2] / GeometryTools.Mm);
    }

    private static FamilyInstance Create(Document doc, FamilySymbol symbol, Placement p)
    {
        if (p.Curve != null)
        {
            var beam = symbol.Category?.Id == new ElementId(BuiltInCategory.OST_StructuralFraming);
            return doc.Create.NewFamilyInstance(p.Curve, symbol, p.Level, beam ? StructuralType.Beam : StructuralType.NonStructural);
        }
        var kind = symbol.Category?.Id == new ElementId(BuiltInCategory.OST_StructuralColumns) ? StructuralType.Column
            : symbol.Category?.Id == new ElementId(BuiltInCategory.OST_StructuralFoundation) ? StructuralType.Footing
            : StructuralType.NonStructural;
        return p.Host != null
            ? doc.Create.NewFamilyInstance(p.Point, symbol, p.Host, p.Level, kind)
            : doc.Create.NewFamilyInstance(p.Point, symbol, p.Level, kind);
    }
}
