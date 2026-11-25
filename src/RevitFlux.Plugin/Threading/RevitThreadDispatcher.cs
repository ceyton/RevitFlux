using System;
using System.Diagnostics;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitFlux.Core.Results;
using RevitFlux.Core.Session;
using RevitFlux.Core.Threading;
using RevitFlux.Core.Tools;

namespace RevitFlux.Plugin.Threading;

public class RevitThreadDispatcher
{
    private const int RaiseIntervalMs = 1000;
    private readonly ToolRequestQueue _queue;
    private readonly ExternalEvent _externalEvent;

    public RevitThreadDispatcher(ToolExecutionHandler handler, ExternalEvent externalEvent)
    {
        _queue = handler.Queue;
        _externalEvent = externalEvent;
    }

    public CortexResult<object> Execute(ICortexTool tool, JObject input, CortexSession session,
        int timeoutMs = 120000)
    {
        var request = _queue.Enqueue(tool, input, session);
        var clock = Stopwatch.StartNew();

        while (true)
        {
            // Pending is fine: the already-raised event drains the whole queue. Raising
            // again while queued covers an event that fired just before we enqueued.
            if (_queue.GetState(request) == ToolRequestState.Queued)
                _externalEvent.Raise();

            var remaining = timeoutMs - clock.ElapsedMilliseconds;
            if (remaining <= 0) break;
            if (request.Wait((int)Math.Min(RaiseIntervalMs, remaining)))
                return Completed(request);
        }

        switch (_queue.Cancel(request))
        {
            case ToolRequestState.Completed:
                return Completed(request);
            case ToolRequestState.Withdrawn:
                return CortexResult<object>.Fail(CortexErrorCode.Timeout,
                    $"Tool '{tool.Name}' did NOT run: Revit did not become available within {timeoutMs}ms",
                    suggestion: "Revit is busy, in an edit mode, or showing a dialog. The request was withdrawn, so retrying is safe once Revit is idle.");
            default:
                return CortexResult<object>.Fail(CortexErrorCode.Timeout,
                    $"Tool '{tool.Name}' started but did not finish within {timeoutMs}ms",
                    suggestion: "Check Revit for an open confirmation dialog; answering it now cancels the unconfirmed step. Work already running may still complete (logged as completed_after_timeout), so verify the model before retrying.");
        }
    }

    private static CortexResult<object> Completed(ToolRequest request) =>
        request.Result ?? CortexResult<object>.Fail(CortexErrorCode.Unknown, "No result from tool execution");
}
