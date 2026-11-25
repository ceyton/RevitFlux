using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;
using RevitFlux.Core.Results;
using RevitFlux.Core.Session;
using RevitFlux.Core.Tools;

namespace RevitFlux.Core.Threading;

public enum ToolRequestState { Queued, Running, Completed, Withdrawn, Abandoned }

/// <summary>One tool call. Owns its result and completion signal, so requests cannot
/// overwrite each other's outcome.</summary>
public sealed class ToolRequest
{
    internal ToolRequest(ICortexTool tool, JObject input, CortexSession session)
    {
        Tool = tool;
        Input = input;
        Session = session;
    }

    public ICortexTool Tool { get; }
    public JObject Input { get; }
    public CortexSession Session { get; }
    public CortexResult<object>? Result { get; internal set; }
    internal ToolRequestState State = ToolRequestState.Queued;
    // Not disposed explicitly: an abandoned request may still be signalled later.
    internal readonly ManualResetEvent Done = new ManualResetEvent(false);

    public bool Wait(int milliseconds) => Done.WaitOne(milliseconds);
}

/// <summary>
/// FIFO of tool calls: socket threads enqueue, the Revit thread drains.
/// Revit-free so the timeout races can be unit tested.
/// </summary>
public sealed class ToolRequestQueue
{
    private readonly object _lock = new object();
    private readonly Queue<ToolRequest> _queue = new Queue<ToolRequest>();
    private readonly Action<ToolRequest, CortexResult<object>>? _completedAfterTimeout;

    public ToolRequestQueue(Action<ToolRequest, CortexResult<object>>? completedAfterTimeout = null)
    {
        _completedAfterTimeout = completedAfterTimeout;
    }

    public ToolRequest Enqueue(ICortexTool tool, JObject input, CortexSession session)
    {
        var request = new ToolRequest(tool, input, session);
        lock (_lock) _queue.Enqueue(request);
        return request;
    }

    public ToolRequestState GetState(ToolRequest request)
    {
        lock (_lock) return request.State;
    }

    /// <summary>
    /// Stop waiting for a request. Withdrawn means it never started and never will;
    /// Abandoned means it is running now; Completed means the result is available.
    /// </summary>
    public ToolRequestState Cancel(ToolRequest request)
    {
        lock (_lock)
        {
            if (request.State == ToolRequestState.Queued) request.State = ToolRequestState.Withdrawn;
            else if (request.State == ToolRequestState.Running) request.State = ToolRequestState.Abandoned;
            return request.State;
        }
    }

    /// <summary>Runs every queued request. Call on the Revit thread only.</summary>
    public void Drain()
    {
        while (true)
        {
            ToolRequest request;
            lock (_lock)
            {
                if (_queue.Count == 0) return;
                request = _queue.Dequeue();
                if (request.State != ToolRequestState.Queued) continue;
                request.State = ToolRequestState.Running;
            }
            Run(request);
        }
    }

    private void Run(ToolRequest request)
    {
        CortexResult<object> result;
        var previous = request.Session.IsExecutionAbandoned;
        request.Session.IsExecutionAbandoned = () => GetState(request) == ToolRequestState.Abandoned;
        try
        {
            result = request.Tool.Execute(request.Input, request.Session);
        }
        catch (Exception ex)
        {
            result = CortexResult<object>.Fail(CortexErrorCode.Unknown, $"Unhandled exception: {ex.Message}");
        }
        finally
        {
            request.Session.IsExecutionAbandoned = previous;
        }

        bool abandoned;
        lock (_lock)
        {
            abandoned = request.State == ToolRequestState.Abandoned;
            if (!abandoned)
            {
                request.Result = result;
                request.State = ToolRequestState.Completed;
            }
        }
        request.Done.Set();
        if (abandoned)
        {
            try { _completedAfterTimeout?.Invoke(request, result); } catch { }
        }
    }
}
