using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitFlux.Server.Tools;

/// <summary>
/// Local log of failed calls, scripts and reported problems (~/.revitflux/issues, one file per day).
/// It holds project data, so it is never sent anywhere.
/// </summary>
[McpServerToolType]
public static class IssueLog
{
    private static readonly object Gate = new();
    // Any error field with a value, including per-element errors inside a successful reply.
    // Lists of three or more rows become tables, where "error" is a column name.
    private static readonly Regex ErrorField = new(@"""errors?"":(?!null|""""|\[\]|\{\}|0[,}]|false)|""errors?""[,\]]|""success"":false", RegexOptions.Compiled);

    private static string Folder => Path.Combine(
        Environment.GetEnvironmentVariable("REVITFLUX_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".revitflux"),
        "issues");

    [McpServerTool(Name = "log_issue"), Description("Record a RevitFlux problem locally for later fixing: wrong result, missing tool, confusing behavior. Failed calls are logged automatically.")]
    public static string Report(
        [Description("Tool involved, or 'missing tool'")] string tool,
        [Description("What happened")] string problem,
        [Description("What was expected, or the workaround used")] string? expected = null)
        => Write(new JObject { ["kind"] = "reported", ["tool"] = tool, ["problem"] = problem, ["expected"] = expected })
            ? "logged" : "could not write the issue log";

    public static async ValueTask<CallToolResult> Watch(
        RequestContext<CallToolRequestParams> request,
        Func<ValueTask<CallToolResult>> call)
    {
        // run_tool replaces the request, so read it first.
        var name = request.Params?.Name ?? "";
        var args = request.Params?.Arguments == null ? "" : System.Text.Json.JsonSerializer.Serialize(request.Params.Arguments);
        if (name == "run_tool" && request.Params!.Arguments!.TryGetValue("name", out var inner) && inner.ValueKind == JsonValueKind.String)
            name = inner.GetString()!;

        var watch = Stopwatch.StartNew();
        try
        {
            var result = await call();
            var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
            var kind = Classify(name, result.IsError == true, text);
            if (kind != null) Record(kind, name, args, text, watch);
            return result;
        }
        catch (OperationCanceledException)
        {
            Record("cancelled", name, args, "cancelled by the client", watch);
            throw;
        }
        catch (Exception ex)
        {
            Record("error", name, args, ex.ToString(), watch);
            throw;
        }
    }

    public static string? Classify(string tool, bool isError, string text)
    {
        if (isError || ErrorField.IsMatch(text)) return "error";
        // Every script shows a job that may deserve a dedicated tool.
        return tool == "send_code_to_revit" ? "script" : null;
    }

    private static void Record(string kind, string tool, string args, string result, Stopwatch watch) => Write(new JObject
    {
        ["kind"] = kind,
        ["tool"] = tool,
        ["args"] = Cut(args, 4000),
        ["result"] = Cut(kind == "script" ? "" : result, 3000),
        ["ms"] = watch.ElapsedMilliseconds
    });

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private static bool Write(JObject entry)
    {
        try
        {
            entry.AddFirst(new JProperty("version", typeof(IssueLog).Assembly.GetName().Version?.ToString(3)));
            entry.AddFirst(new JProperty("ts", DateTime.Now.ToString("s")));
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, $"{DateTime.Now:yyyy-MM-dd}.jsonl"), entry.ToString(Formatting.None) + "\n");
            }
            return true;
        }
        catch (Exception)
        {
            return false; // logging must never break a tool call
        }
    }
}
