using Newtonsoft.Json.Linq;
using RevitFlux.Core.Results;
using RevitFlux.Core.Session;
using RevitFlux.Core.Tools;

namespace RevitFlux.Tools.Meta;

[ToolSafety(true, false)]
public class SayHelloTool : ICortexTool
{
    public string Name => "say_hello";
    public string Category => "Meta";
    public bool RequiresDocument => false;
    public bool IsDynamic => false;
    public string Description => "Say Hello";
    public CortexResult<object> Execute(JObject input, CortexSession session)
    {
        var message = input["message"]?.ToString() ?? "Hello from RevitFlux!";

        return CortexResult<object>.Ok(new
        {
            message,
            locale = session.DetectedLocale,
            toolCount = "RevitFlux is running"
        });
    }
}
