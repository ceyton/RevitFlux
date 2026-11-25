using System.IO;
using Xunit;

namespace RevitFlux.Tests.Router;

/// <summary>
/// Source-text assertions for the inline UI-thread guard in CortexRouter (audit
/// 2026-06-11). The inline path runs a tool on the UI thread but OUTSIDE a Revit
/// API context (modeless WPF handlers): a tool
/// that opens a Transaction there throws deep inside Revit, so only read-only
/// tools may run inline.
/// The dispatcher==null branch (unit tests) is intentionally NOT guarded.
/// </summary>
public class RouterInlineGuardSourceTests
{
    private static string ReadRouter()
    {
        return File.ReadAllText(Path.GetFullPath(Path.Combine(
            "..", "..", "..", "..", "RevitFlux.Plugin", "CortexRouter.cs")));
    }

    [Fact]
    public void InlinePath_RejectsWriteTools()
    {
        var src = ReadRouter();
        Assert.Contains("onUiThread && !IsToolReadOnly(toolName)", src);
        Assert.Contains("cannot run inline on the UI thread", src);
    }
}
