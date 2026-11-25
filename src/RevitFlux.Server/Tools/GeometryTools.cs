using System.ComponentModel;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using RevitFlux.Server.Connection;

namespace RevitFlux.Server.Tools;

[McpServerToolType]
public static class GeometryTools
{
    [McpServerTool(Name = "get_geometry", ReadOnly = true), Description("Read geometry of host or linked elements in host mm. mode location: points, curves, boxes. faces: outlines of horizontal faces. slice: section cut at z (plan x,y) or along line (u along the line, v = height). Paths: [[x,y],...]; [x,y,mx,my] = the segment starting there is an arc through m. Reusable in create_detail_items.")]
    public static async Task<string> GetGeometry(
        RevitConnectionManager revit,
        [Description("location | faces | slice")] string mode = "location",
        [Description("Element IDs (in the link if linkId is set), or use categories")] long[]? elementIds = null,
        [Description("Categories, e.g. OST_StructuralFraming")] string[]? categories = null,
        [Description("Only types whose name contains this")] string? typeContains = null,
        [Description("Link instance ID")] long? linkId = null,
        [Description("JSON [minX,minY,maxX,maxY] or with Z [minX,minY,minZ,maxX,maxY,maxZ]")] string? region = null,
        [Description("slice: elevation mm")] double? z = null,
        [Description("slice: JSON [[x,y],[x,y]]")] string? line = null,
        [Description("faces: bottom | top")] string side = "bottom",
        [Description("Skip smaller loops")] double? minAreaM2 = null,
        [Description("Skip larger loops")] double? maxAreaM2 = null,
        [Description("Default 100")] int? maxElements = null,
        CancellationToken ct = default)
    {
        var p = new JObject { ["mode"] = mode, ["side"] = side };
        if (elementIds != null) p["elementIds"] = new JArray(elementIds);
        if (categories != null) p["categories"] = new JArray(categories);
        if (typeContains != null) p["typeContains"] = typeContains;
        if (linkId != null) p["linkId"] = linkId;
        if (region != null) p["region"] = JArray.Parse(region);
        if (z != null) p["z"] = z;
        if (line != null) p["line"] = JArray.Parse(line);
        if (minAreaM2 != null) p["minAreaM2"] = minAreaM2;
        if (maxAreaM2 != null) p["maxAreaM2"] = maxAreaM2;
        if (maxElements != null) p["maxElements"] = maxElements;
        var result = await revit.ExecuteAsync("get_geometry", p, ct);
        return result.ToLean();
    }

    [McpServerTool(Name = "create_detail_items"), Description("Draw many items in one view, one undo step, all or nothing. mm; plans use model x,y, other views their own right/up axes. Items: {kind:region,loops:[path],style} {kind:line,path,closed?,style} {kind:circle,at,radius,style} {kind:text,text,at,leaders?:[[x,y]],width?,style} {kind:symbol,type|typeId,at,rotation?}. Paths as in get_geometry; text width is paper mm.")]
    public static async Task<string> CreateDetailItems(
        RevitConnectionManager revit,
        [Description("JSON array of items")] string items,
        [Description("Default: active view")] long? viewId = null,
        [Description("Comments value for all items")] string? comment = null,
        [Description("Validate only")] bool dryRun = false,
        CancellationToken ct = default)
    {
        var p = new JObject { ["items"] = JArray.Parse(items), ["dryRun"] = dryRun };
        if (viewId != null) p["viewId"] = viewId;
        if (comment != null) p["comment"] = comment;
        var result = await revit.ExecuteAsync("create_detail_items", p, ct);
        return result.ToLean();
    }

    [McpServerTool(Name = "place_instances"), Description("Place many instances of one family type, one undo step, all or nothing. Placements: {at:[x,y,z], rotation?, levelId?, hostId?, flipFacing?, flipHand?} or {line:[[x,y,z],[x,y,z]]} for beams (3 points = arc through the middle). Absolute mm.")]
    public static async Task<string> PlaceInstances(
        RevitConnectionManager revit,
        [Description("JSON array of placements")] string placements,
        [Description("'Family: Type', type or family name")] string? type = null,
        [Description("Type ID")] long? typeId = null,
        [Description("Comments value for all")] string? comment = null,
        [Description("Validate only")] bool dryRun = false,
        CancellationToken ct = default)
    {
        var p = new JObject { ["placements"] = JArray.Parse(placements), ["dryRun"] = dryRun };
        if (type != null) p["type"] = type;
        if (typeId != null) p["typeId"] = typeId;
        if (comment != null) p["comment"] = comment;
        var result = await revit.ExecuteAsync("place_instances", p, ct);
        return result.ToLean();
    }

    [McpServerTool(Name = "create_family"), Description("Build and load a simple new family from vertical extrusions. Saved in ~/.revitflux/families. solids: [{loops:[path]|circle:[x,y,radius], z, height, void?, material?}] in mm; paths as in get_geometry.")]
    public static async Task<string> CreateFamily(
        RevitConnectionManager revit,
        [Description("New family name")] string name,
        [Description("JSON array of solids")] string solids,
        [Description("Default OST_GenericModel")] string? category = null,
        [Description("Type name")] string? typeName = null,
        [Description("JSON object of existing family parameters (lengths in mm)")] string? parameters = null,
        CancellationToken ct = default)
    {
        var p = new JObject { ["name"] = name, ["solids"] = JArray.Parse(solids) };
        if (category != null) p["category"] = category;
        if (typeName != null) p["typeName"] = typeName;
        if (parameters != null) p["parameters"] = JObject.Parse(parameters);
        var result = await revit.ExecuteAsync("create_family", p, ct);
        return result.ToLean();
    }

    [McpServerTool(Name = "explain_visibility", ReadOnly = true), Description("Why elements are hidden or not visible in a view: category, filter, workset, phase, crop, view range, section box, design option.")]
    public static async Task<string> ExplainVisibility(
        RevitConnectionManager revit,
        [Description("Element IDs (max 200)")] long[] elementIds,
        [Description("Default: active view")] long? viewId = null,
        CancellationToken ct = default)
    {
        var p = new JObject { ["elementIds"] = new JArray(elementIds) };
        if (viewId != null) p["viewId"] = viewId;
        var result = await revit.ExecuteAsync("explain_visibility", p, ct);
        return result.ToLean();
    }
}
