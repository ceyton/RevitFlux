using Autodesk.Revit.UI;
using RevitFlux.Core.Security;
using RevitFlux.Core.Threading;

namespace RevitFlux.Plugin.Threading;

/// <summary>Drains <see cref="ToolRequestQueue"/> on the Revit thread.</summary>
public class ToolExecutionHandler : IExternalEventHandler
{
    public ToolExecutionHandler(AuditLogger? auditLogger = null)
    {
        var log = auditLogger ?? new AuditLogger();
        // The caller already received Timeout, but the model may have changed.
        Queue = new ToolRequestQueue((request, result) => log.LogWithPerf(
            request.Tool.Name,
            "completed_after_timeout (result discarded; model may have changed)",
            result.Success, result.Error?.Code, errorMessage: result.Error?.Message));
    }

    public ToolRequestQueue Queue { get; }

    public void Execute(UIApplication app) => Queue.Drain();

    public string GetName() => "RevitFlux Tool Execution";
}
