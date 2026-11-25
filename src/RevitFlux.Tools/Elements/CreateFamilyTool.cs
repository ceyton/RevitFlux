using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitFlux.Core.Geometry;
using RevitFlux.Core.Results;
using RevitFlux.Core.Security;
using RevitFlux.Core.Session;
using RevitFlux.Core.Tools;
using RevitFlux.Tools.Utilities;

namespace RevitFlux.Tools.Elements;

/// <summary>Builds a simple loadable family from extrusions and loads it into the model.</summary>
[ToolSafety(false, false)]
public class CreateFamilyTool : ICortexTool, ICommandTimeoutTool
{
    public string Name => "create_family";
    public string Category => "Elements";
    public bool RequiresDocument => true;
    public bool IsDynamic => false;
    public int CommandTimeoutSeconds => 280;
    public string Description => "Builds a simple family from vertical extrusions and cylinders and loads it into the model.";

    public CortexResult<object> Execute(JObject input, CortexSession session)
    {
        var (doc, error) = ToolHelpers.RequireDocument(session);
        if (error != null) return error;

        Document? family = null;
        try
        {
            var name = input["name"]?.Value<string>()?.Trim() ?? "";
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return Invalid("name must be a valid file name");
            if (new FilteredElementCollector(doc).OfClass(typeof(Family)).Any(f => GeometryTools.Same(f.Name, name)))
                return Invalid($"family '{name}' is already loaded; choose another name");
            if (input["solids"] is not JArray solids || solids.Count == 0)
                return Invalid("solids must be a non-empty array");

            var categoryName = input["category"]?.Value<string>() ?? "OST_GenericModel";
            var category = CategoryResolver.Resolve(categoryName);
            if (category == null) return Invalid($"category '{categoryName}' not found");

            var folder = Path.Combine(CortexSettings.HomeFolder, "families");
            var path = Path.Combine(folder, name + ".rfa");
            if (File.Exists(path)) return Invalid($"{path} already exists; choose another name");

            var template = FindTemplate(doc!.Application.FamilyTemplatePath);
            if (template == null) return Invalid("no 'Generic Model' family template is installed");

            // Parse everything before opening the template.
            var shapes = solids.Select((s, i) => Parse(s as JObject, i)).ToList();

            family = doc.Application.NewFamilyDocument(template);
            using (var tx = new Transaction(family, "RevitFlux: Create Family"))
            {
                var failures = TransactionFailureHandling.SuppressWarnings(tx);
                tx.Start();
                var target = family.Settings.Categories.get_Item(category.Value);
                if (family.OwnerFamily.FamilyCategory.Id != target.Id) family.OwnerFamily.FamilyCategory = target;

                var materials = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in shapes)
                {
                    var plane = SketchPlane.Create(family, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, s.Z)));
                    var extrusion = family.FamilyCreate.NewExtrusion(!s.Void, s.Profile, plane, s.Height);
                    if (s.Material != null && !s.Void)
                    {
                        if (!materials.TryGetValue(s.Material, out var id))
                            materials[s.Material] = id = FindMaterial(family, s.Material);
                        extrusion.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(id);
                    }
                }

                var fm = family.FamilyManager;
                var typeName = input["typeName"]?.Value<string>() ?? name;
                if (fm.CurrentType == null) fm.NewType(typeName); else fm.RenameCurrentType(typeName);
                if (input["parameters"] is JObject values)
                {
                    foreach (var v in values.Properties()) SetParameter(fm, v.Name, v.Value);
                }
                if (tx.Commit() != TransactionStatus.Committed)
                    return CortexResult<object>.Fail(CortexErrorCode.TransactionFailed,
                        $"Revit rolled back: {TransactionFailureHandling.Describe(failures)}");
            }

            Directory.CreateDirectory(folder);
            family.SaveAs(path);
            var loaded = family.LoadFamily(doc);
            var symbolId = loaded.GetFamilySymbolIds().First();
            return CortexResult<object>.Ok(new
            {
                familyId = ToolHelpers.GetElementIdValue(loaded.Id),
                typeId = ToolHelpers.GetElementIdValue(symbolId),
                solidCount = shapes.Count,
                path
            });
        }
        catch (ArgumentException ex)
        {
            return Invalid(ex.Message);
        }
        catch (Exception ex)
        {
            return CortexResult<object>.Fail(CortexErrorCode.Unknown, $"create_family failed: {ex.Message}");
        }
        finally
        {
            try { family?.Close(false); }
            catch (Exception) { /* nothing useful to do */ }
        }
    }

    private sealed class Shape
    {
        public CurveArrArray Profile = new();
        public double Z;
        public double Height;
        public bool Void;
        public string? Material;
    }

    private static CortexResult<object> Invalid(string message) =>
        CortexResult<object>.Fail(CortexErrorCode.InvalidInput, message);

    private static Shape Parse(JObject? item, int index)
    {
        var at = $"solids[{index}]";
        if (item == null) throw new ArgumentException($"{at} must be an object");
        var height = item["height"]?.Value<double?>() ?? 0;
        if (Math.Abs(height) < 1) throw new ArgumentException($"{at}: height must be at least 1 mm");
        var shape = new Shape
        {
            Z = (item["z"]?.Value<double?>() ?? 0) / GeometryTools.Mm,
            Height = height / GeometryTools.Mm,
            Void = item["void"]?.Value<bool>() ?? false,
            Material = item["material"]?.Value<string>()
        };
        Func<double, double, XYZ> map = (x, y) => new XYZ(x / GeometryTools.Mm, y / GeometryTools.Mm, shape.Z);
        if (item["circle"] != null)
        {
            var c = Path2D.Numbers(item["circle"], $"{at}.circle", 3);
            if (c[2] <= 0) throw new ArgumentException($"{at}: circle is [x,y,radius]");
            shape.Profile.Append(ToArray(GeometryTools.Circle(c[0], c[1], c[2], map)));
        }
        if (item["loops"] is JArray loops)
        {
            foreach (var l in loops) shape.Profile.Append(ToArray(GeometryTools.ToCurves(Path2D.Parse(l, true, $"{at}.loops"), map)));
        }
        if (shape.Profile.Size == 0) throw new ArgumentException($"{at}: needs loops or circle [x,y,radius]");
        return shape;
    }

    private static CurveArray ToArray(IEnumerable<Curve> curves)
    {
        var a = new CurveArray();
        foreach (var c in curves) a.Append(c);
        return a;
    }

    private static string? FindTemplate(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
        var files = Directory.GetFiles(root, "*Generic Model.rft", SearchOption.AllDirectories);
        return files.OrderByDescending(f => Path.GetFileName(f).StartsWith("Metric", StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f.Length).FirstOrDefault();
    }

    private static ElementId FindMaterial(Document family, string name)
    {
        var hit = new FilteredElementCollector(family).OfClass(typeof(Material)).FirstOrDefault(m => GeometryTools.Same(m.Name, name));
        return hit?.Id ?? Material.Create(family, name);
    }

    private static void SetParameter(FamilyManager fm, string name, JToken value)
    {
        var p = fm.Parameters.Cast<FamilyParameter>().FirstOrDefault(x => GeometryTools.Same(x.Definition.Name, name))
            ?? throw new ArgumentException($"parameter '{name}' not found in the family template");
        if (p.IsReadOnly || p.IsDeterminedByFormula) throw new ArgumentException($"parameter '{name}' is read-only");
        switch (p.StorageType)
        {
            case StorageType.String: fm.Set(p, value.ToString()); break;
            case StorageType.Integer: fm.Set(p, value.Value<int>()); break;
            case StorageType.Double:
                var d = value.Value<double>();
                fm.Set(p, p.Definition.GetDataType() == SpecTypeId.Length ? d / GeometryTools.Mm : d);
                break;
            default: throw new ArgumentException($"parameter '{name}' cannot be set");
        }
    }
}
