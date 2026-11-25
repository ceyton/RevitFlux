using RevitFlux.Core.Discovery;

namespace RevitFlux.Tests.Router;

public class FakeAnalyzer : IDocumentAnalyzer
{
    public bool HasWorksets { get; set; }

    public void Analyze(object document, DocumentCapabilities capabilities)
    {
        capabilities.HasWorksets = HasWorksets;
        if (HasWorksets)
        {
            capabilities.EnableTool("get_worksets");
        }
    }
}
