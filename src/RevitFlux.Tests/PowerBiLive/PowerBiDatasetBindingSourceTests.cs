using System.IO;
using Xunit;

namespace RevitFlux.Tests.PowerBiLive;

public class PowerBiDatasetBindingSourceTests
{
    private static readonly string SchemaPath = Path.GetFullPath(
        Path.Combine("..", "..", "..", "..",
            "RevitFlux.Plugin", "PowerBiLive", "PowerBiDatasetSchema.cs"));

    [Fact]
    public void DatasetName_IsDerivedFromCurrentSchemaVersion()
    {
        var source = File.ReadAllText(SchemaPath);

        Assert.Contains("BuildDefaultDatasetName", source);
        Assert.Contains("CurrentVersion", source);
        Assert.DoesNotContain("RevitFlux Live - v1\"", source);
        Assert.DoesNotContain("- v1\";", source);
    }
}
