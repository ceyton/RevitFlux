using System;
using Newtonsoft.Json.Linq;
using RevitFlux.Core.Geometry;
using Xunit;

namespace RevitFlux.Tests.Elements;

public class Path2DTests
{
    [Fact]
    public void OpenPath_HasOneSegmentLessThanPoints() =>
        Assert.Equal(2, Path2D.Parse(JArray.Parse("[[0,0],[1,0],[1,1]]"), false, "p").Count);

    [Theory]
    [InlineData("[[0,0],[1,0],[1,1]]")]
    [InlineData("[[0,0],[1,0],[1,1],[0,0]]")]
    public void ClosedPath_ClosesOnce(string json)
    {
        var s = Path2D.Parse(JArray.Parse(json), true, "p");
        Assert.Equal(3, s.Count);
        Assert.Equal(0, s[2].X1);
        Assert.Equal(0, s[2].Y1);
    }

    [Fact]
    public void FourNumbers_MakeAnArcThroughTheMiddle()
    {
        var s = Path2D.Parse(JArray.Parse("[[0,0,50,50],[100,0,50,-50]]"), true, "p");
        Assert.Equal(2, s.Count);
        Assert.True(s[0].IsArc);
        Assert.Equal(50, s[0].My);
        Assert.Equal(-50, s[1].My);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[[0,0]]")]
    [InlineData("[[0,0],[1]]")]
    [InlineData("[[0,0],[1,2,3]]")]
    [InlineData("[[0,0],[\"1\",2],[3,3]]")]
    [InlineData("[[0,0],[1,0]]")]
    [InlineData("\"x\"")]
    public void BadClosedPath_IsRejected(string json) =>
        Assert.Throws<ArgumentException>(() => Path2D.Parse(JToken.Parse(json), true, "p"));
}
