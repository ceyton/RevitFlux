using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RevitFlux.Server.Connection;
using RevitFlux.Server.Tools;

var port = RevitConnectionManager.ResolvePort();

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(new RevitConnectionManager(port));
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "RevitFlux",
            Version = "0.2.0"
        };
        options.ServerInstructions =
            "Core Revit tools are listed; use find_tools then run_tool for everything else. Query narrowly: filter by " +
            "category and limit counts. Lists come as tables: \"same\" holds values shared by every row. A _preview result is " +
            "incomplete. Use dryRun before destructive or bulk changes. send_code_to_revit is a LAST RESORT: only when no " +
            "dedicated tool fits, and after the user's explicit consent. " +
            "Failed calls are logged automatically; for a wrong result or missing tool, run_tool log_issue {tool, problem, expected}.";
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => (request, ct) =>
        IssueLog.Watch(request, () => next(request, ct))))
    .WithRequestFilters(filters => filters.AddListToolsFilter(next => async (request, ct) =>
    {
        var result = await next(request, ct);
        result.Tools = result.Tools.Where(t => ToolCatalog.Core.Contains(t.Name)).ToList();
        return result;
    }));
await builder.Build().RunAsync();
