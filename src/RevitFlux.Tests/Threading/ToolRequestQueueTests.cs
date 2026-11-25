using Newtonsoft.Json.Linq;
using RevitFlux.Core.Results;
using RevitFlux.Core.Session;
using RevitFlux.Core.Threading;
using RevitFlux.Core.Tools;
using Xunit;

namespace RevitFlux.Tests.Threading;

public class ToolRequestQueueTests
{
    private sealed class Tool : ICortexTool
    {
        private readonly Func<CortexSession, CortexResult<object>> _run;
        public Tool(string name, Func<CortexSession, CortexResult<object>> run) { Name = name; _run = run; }
        public string Name { get; }
        public string Category => "Test";
        public bool RequiresDocument => false;
        public bool IsDynamic => false;
        public string Description => "";
        public CortexResult<object> Execute(JObject input, CortexSession session) => _run(session);
    }

    private readonly CortexSession _session = new(new SessionStore());

    private static CortexResult<object> Ok(string v) => CortexResult<object>.Ok(v);

    [Fact]
    public void Requests_KeepTheirOwnResults()
    {
        var q = new ToolRequestQueue();
        var a = q.Enqueue(new Tool("a", _ => Ok("A")), new JObject(), _session);
        var b = q.Enqueue(new Tool("b", _ => Ok("B")), new JObject(), _session);
        q.Drain();
        Assert.True(a.Wait(0));
        Assert.True(b.Wait(0));
        Assert.Equal("A", a.Result!.Data);
        Assert.Equal("B", b.Result!.Data);
    }

    [Fact]
    public void Withdrawn_NeverRuns_AndDoesNotBlockTheNext()
    {
        var ran = new List<string>();
        var q = new ToolRequestQueue();
        var a = q.Enqueue(new Tool("a", _ => { ran.Add("a"); return Ok(""); }), new JObject(), _session);
        q.Enqueue(new Tool("b", _ => { ran.Add("b"); return Ok(""); }), new JObject(), _session);
        Assert.Equal(ToolRequestState.Withdrawn, q.Cancel(a));
        q.Drain();
        Assert.Equal(new[] { "b" }, ran);
    }

    [Fact]
    public void Abandoned_ConfirmationFailsClosed_AndCompletionIsReported()
    {
        var reported = new List<string>();
        var q = new ToolRequestQueue((r, _) => reported.Add(r.Tool.Name));
        ToolRequest? request = null;
        var confirmed = true;
        // The user clicks Yes only after the caller has already timed out.
        _session.CriticalConfirmAction = (_, _, _) => { q.Cancel(request!); return true; };
        request = q.Enqueue(new Tool("a", s =>
        {
            confirmed = s.RequestConfirmation("run", 1, critical: true);
            return Ok("");
        }), new JObject(), _session);
        q.Drain();
        Assert.False(confirmed);
        Assert.Null(request.Result);
        Assert.Equal(new[] { "a" }, reported);
        Assert.Null(_session.IsExecutionAbandoned);
    }

    [Fact]
    public void Abandoned_NonCriticalConfirmation_AlsoFailsClosed()
    {
        var q = new ToolRequestQueue();
        ToolRequest? request = null;
        var confirmed = true;
        _session.ConfirmAction = (_, _, _) => { q.Cancel(request!); return true; };
        request = q.Enqueue(new Tool("a", s =>
        {
            confirmed = s.RequestConfirmation("delete", 1);
            return Ok("");
        }), new JObject(), _session);
        q.Drain();
        Assert.False(confirmed);
    }

    [Fact]
    public void Exception_BecomesFailure()
    {
        var q = new ToolRequestQueue();
        var a = q.Enqueue(new Tool("a", _ => throw new InvalidOperationException("x")), new JObject(), _session);
        q.Drain();
        Assert.False(a.Result!.Success);
    }

    [Fact]
    public void CancelAfterCompletion_KeepsTheResult()
    {
        var q = new ToolRequestQueue();
        var a = q.Enqueue(new Tool("a", _ => Ok("A")), new JObject(), _session);
        q.Drain();
        Assert.Equal(ToolRequestState.Completed, q.Cancel(a));
        Assert.Equal("A", a.Result!.Data);
    }

    [Theory]
    [InlineData(null, ScriptTransactionMode.Auto)]
    [InlineData("auto", ScriptTransactionMode.Auto)]
    [InlineData("manual", ScriptTransactionMode.Manual)]
    [InlineData("none", ScriptTransactionMode.Manual)]
    [InlineData("group", ScriptTransactionMode.Group)]
    [InlineData("ReadOnly", ScriptTransactionMode.ReadOnly)]
    public void TransactionMode_Parses(string? value, ScriptTransactionMode expected)
    {
        Assert.True(ScriptTransactionModes.TryParse(value, out var mode));
        Assert.Equal(expected, mode);
    }

    [Fact]
    public void TransactionMode_RejectsUnknown() =>
        Assert.False(ScriptTransactionModes.TryParse("read_only", out _));
}
