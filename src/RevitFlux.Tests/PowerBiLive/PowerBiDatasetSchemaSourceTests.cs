using System.IO;
using Xunit;

namespace RevitFlux.Tests.PowerBiLive;

public class PowerBiDatasetSchemaSourceTests
{
    private static readonly string SourcePath = Path.GetFullPath(
        Path.Combine("..", "..", "..", "..",
            "RevitFlux.Plugin", "PowerBiLive", "PowerBiDatasetSchema.cs"));

    [Fact]
    public void ScheduleSchema_IncludesElementIdentityColumns()
    {
        var source = File.ReadAllText(SourcePath);

        Assert.Contains("public const string CurrentVersion = \"1.1\";", source);
        Assert.Contains("Col(\"ElementId\",", source);
        Assert.Contains("Col(\"UniqueId\",", source);
        Assert.True(
            source.IndexOf("Col(\"ElementId\",", System.StringComparison.Ordinal) <
            source.IndexOf("Col(\"ColumnName\",", System.StringComparison.Ordinal));
    }
}
