using RevitFlux.Server.Tools;
using Xunit;

namespace RevitFlux.Tests.Server;

public class IssueLogTests
{
    [Theory]
    [InlineData("x", true, "bad argument", "error")]
    [InlineData("x", false, "{\"success\":false,\"error\":{\"code\":\"Timeout\"}}", "error")]
    [InlineData("x", false, "{\"elements\":[{\"elementId\":9,\"error\":\"not found\"}]}", "error")]
    [InlineData("x", false, "{\"error\":null,\"errors\":[]}", null)]
    [InlineData("x", false, "{\"columns\":[\"id\",\"error\"]}", "error")]
    [InlineData("x", false, "{\"count\":3}", null)]
    [InlineData("send_code_to_revit", false, "{\"ok\":1}", "script")]
    public void Classify(string tool, bool isError, string text, string? expected) =>
        Assert.Equal(expected, IssueLog.Classify(tool, isError, text));
}
