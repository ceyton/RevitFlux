using System.Collections.Generic;
using RevitFlux.Tools.Interop;
using Xunit;

namespace RevitFlux.Tests.Interop;

public class HostLinkResolverTests
{
    [Fact]
    public void NormalizeBasename_LowercasesAndStripsPath()
    {
        Assert.Equal("strutture.rvt", HostLinkResolver.NormalizeBasename(@"C:\Models\Strutture.rvt"));
        Assert.Equal("strutture.rvt", HostLinkResolver.NormalizeBasename("Strutture.rvt"));
        Assert.Equal("",               HostLinkResolver.NormalizeBasename(null));
        Assert.Equal("",               HostLinkResolver.NormalizeBasename(""));
        Assert.Equal("", HostLinkResolver.NormalizeBasename("   "));
    }
}
