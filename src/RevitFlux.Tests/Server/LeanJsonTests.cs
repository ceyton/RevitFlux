using System.Reflection;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using RevitFlux.Server.Tools;
using Xunit;

namespace RevitFlux.Tests.Server;

public class LeanJsonTests
{
    private static JArray Walls(int n) => new(Enumerable.Range(1, n).Select(i =>
        new JObject { ["id"] = i, ["category"] = "Walls", ["name"] = "W" + i }));

    [Fact]
    public void RepeatedObjects_BecomeATable_WithoutLosingValues()
    {
        var packed = (JObject)LeanJson.Pack(Walls(3));
        Assert.Equal("Walls", packed["same"]!["category"]);
        Assert.Equal(new[] { "id", "name" }, packed["columns"]!.Values<string>());
        Assert.Equal("[[1,\"W1\"],[2,\"W2\"],[3,\"W3\"]]", packed["rows"]!.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public void DifferentKeys_StayAsObjects()
    {
        var a = new JArray(new JObject { ["a"] = 1 }, new JObject { ["a"] = null }, new JObject { ["b"] = 1 });
        Assert.True(JToken.DeepEquals(a, LeanJson.Pack(a)));
    }

    [Fact]
    public void FloatNoise_IsRounded_TinyValuesAreNot()
    {
        Assert.Equal("3000.0", LeanJson.Write(new JValue(2999.9999999999995), 100));
        Assert.Equal(1E-09, LeanJson.Pack(new JValue(1E-09)).Value<double>());
    }

    [Fact]
    public void LargeResult_IsPreviewed_AndSavedInFull_CountersUntouched()
    {
        var text = LeanJson.Write(new JObject { ["count"] = 500, ["items"] = Walls(500) }, 2000);
        var o = JObject.Parse(text);
        Assert.Equal(500, o["count"]);
        Assert.True(text.Length < 2600);
        Assert.Equal($"{o["items"]!["rows"]!.Count()} of 500", o["_preview"]!["shown"]!["items.rows"]);
        var full = JObject.Parse(File.ReadAllText((string)o["_preview"]!["fullResult"]!));
        Assert.Equal(500, full["items"]!["rows"]!.Count());
        Assert.Equal(2, o["items"]!["columns"]!.Count());
    }

    [Fact]
    public void EveryCoreTool_Exists()
    {
        var names = typeof(ToolCatalog).Assembly.GetTypes().SelectMany(t => t.GetMethods())
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name).ToHashSet();
        Assert.All(ToolCatalog.Core, c => Assert.Contains(c, names));
    }
}
