using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RevitFlux.Server.Tools;

/// <summary>
/// Only the core tools are listed to the client. The rest are found with find_tools and run
/// with run_tool, so a session does not pay for schemas it never uses.
/// </summary>
[McpServerToolType]
public static class ToolCatalog
{
    public static readonly HashSet<string> Core = new(StringComparer.Ordinal)
    {
        "find_tools", "run_tool",
        // read
        "get_project_info", "get_current_view_info", "get_current_view_elements", "get_selected_elements",
        "get_element_parameters", "get_element_solid_geometry", "ai_element_filter", "filter_by_parameter_value",
        "get_linked_elements", "get_linked_file_instances", "get_elements_in_spatial_volume",
        "get_available_family_types", "measure_between_elements", "export_elements_data", "get_warnings",
        // write
        "set_element_parameters", "modify_element", "operate_element", "delete_element", "override_graphics",
        "create_filled_regions", "create_line_based_element", "create_dimensions", "create_text_note",
        "send_code_to_revit"
    };

    [McpServerTool(Name = "find_tools", ReadOnly = true), Description("Find more Revit tools (sheets, schedules, rooms, links, materials, families, Excel, worksets, clash...). Returns signatures to use with run_tool. Empty query lists all names.")]
    public static string FindTools(
        RequestContext<CallToolRequestParams> context,
        [Description("Keywords, e.g. 'sheet viewport'")] string query = "")
    {
        var hidden = Hidden(context).ToList();
        var words = query.ToLowerInvariant().Split(new[] { ' ', ',', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return string.Join(", ", hidden.Select(t => t.ProtocolTool.Name).OrderBy(n => n, StringComparer.Ordinal));

        var hits = hidden
            .Select(t => (tool: t, score: Score(t.ProtocolTool, words)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.tool.ProtocolTool.Name, StringComparer.Ordinal)
            .Take(5).ToList();
        if (hits.Count == 0) return "No match. Try other keywords, or an empty query for all names.";

        var sb = new StringBuilder("! = required\n");
        foreach (var hit in hits) sb.Append(Signature(hit.tool.ProtocolTool)).Append('\n');
        return sb.ToString();
    }

    [McpServerTool(Name = "run_tool", Destructive = true), Description("Run a tool returned by find_tools.")]
    public static async Task<CallToolResult> RunTool(
        RequestContext<CallToolRequestParams> context,
        [Description("Tool name")] string name,
        [Description("Arguments object for that tool")] JsonElement? arguments = null,
        CancellationToken ct = default)
    {
        // Core tools must be called directly, so a client rule on e.g. send_code_to_revit
        // cannot be bypassed through run_tool.
        if (Core.Contains(name)) return Error($"'{name}' is a listed tool: call it directly.");
        var tool = Hidden(context).FirstOrDefault(t => t.ProtocolTool.Name == name);
        if (tool == null) return Error($"Unknown tool '{name}'. Use find_tools.");

        var args = new Dictionary<string, JsonElement>();
        if (arguments is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var p in obj.EnumerateObject()) args[p.Name] = p.Value;
        }
        else if (arguments is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            return Error("arguments must be a JSON object.");
        }

        // A misspelled argument would be silently ignored by the binder, so reject it here.
        var schema = tool.ProtocolTool.InputSchema;
        var known = schema.TryGetProperty("properties", out var props)
            ? props.EnumerateObject().Select(p => p.Name).ToHashSet()
            : new HashSet<string>();
        var unknown = args.Keys.Where(k => !known.Contains(k)).ToList();
        var missing = Required(schema).Where(r => !args.ContainsKey(r)).ToList();
        if (unknown.Count > 0 || missing.Count > 0)
            return Error($"Unknown: [{string.Join(", ", unknown)}] Missing: [{string.Join(", ", missing)}]. {Signature(tool.ProtocolTool)}");

        context.Params = new CallToolRequestParams { Name = name, Arguments = args };
        return await tool.InvokeAsync(context, ct);
    }

    private static IEnumerable<McpServerTool> Hidden(RequestContext<CallToolRequestParams> context) =>
        (context.Server.ServerOptions.ToolCollection ?? Enumerable.Empty<McpServerTool>())
        .Where(t => !Core.Contains(t.ProtocolTool.Name));

    private static int Score(Tool tool, string[] words)
    {
        var name = tool.Name.ToLowerInvariant();
        var text = (tool.Description ?? "").ToLowerInvariant();
        var score = 0;
        foreach (var w in words)
        {
            var stem = w.Length > 3 && w.EndsWith('s') ? w[..^1] : w;
            if (name.Contains(stem)) score += 3;
            else if (text.Contains(stem)) score += 1;
        }
        return score;
    }

    private static HashSet<string> Required(JsonElement schema) =>
        schema.TryGetProperty("required", out var r)
            ? r.EnumerateArray().Select(x => x.GetString()!).ToHashSet()
            : new HashSet<string>();

    public static string Signature(Tool tool)
    {
        var required = Required(tool.InputSchema);
        var sb = new StringBuilder(tool.Name).Append(": ").Append(tool.Description);
        if (!tool.InputSchema.TryGetProperty("properties", out var props)) return sb.ToString();
        foreach (var p in props.EnumerateObject())
        {
            sb.Append("\n  ").Append(p.Name).Append(required.Contains(p.Name) ? "!" : "");
            if (p.Value.ValueKind != JsonValueKind.Object) continue;
            if (p.Value.TryGetProperty("type", out var type)) sb.Append(' ').Append(TypeName(type, p.Value));
            if (p.Value.TryGetProperty("description", out var d)) sb.Append(" - ").Append(d.GetString());
        }
        return sb.ToString();
    }

    private static string TypeName(JsonElement type, JsonElement schema)
    {
        var name = type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => x != "null") ?? "any"
            : type.GetString() ?? "any";
        if (name != "array") return name;
        return schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object
               && items.TryGetProperty("type", out var t)
            ? TypeName(t, items) + "[]"
            : "array";
    }

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = new List<ContentBlock> { new TextContentBlock { Text = message } }
    };
}
