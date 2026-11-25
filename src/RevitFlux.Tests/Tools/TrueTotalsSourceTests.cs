using System.IO;
using Xunit;

namespace RevitFlux.Tests.Tools;

/// <summary>A limited list must report the real total and say that it is limited.</summary>
public class TrueTotalsSourceTests
{
    [Theory]
    [InlineData("Project", "GetAvailableFamilyTypesTool.cs", "totalCount = matches.Count")]
    [InlineData("Elements", "ExportRoomDataTool.cs", "totalCount = rooms.Count")]
    [InlineData("Elements", "GetSelectedElementsTool.cs", "selectedCount    = allSelected.Count")]
    [InlineData("Elements", "GetLinkedElementsTool.cs", "totalCount = collector.GetElementCount()")]
    public void LimitedList_ReportsTheRealTotal(string folder, string file, string total)
    {
        var src = File.ReadAllText(Path.GetFullPath(Path.Combine(
            "..", "..", "..", "..", "RevitFlux.Tools", folder, file)));
        Assert.Contains(total, src);
        Assert.Contains("truncated", src);
    }
}
