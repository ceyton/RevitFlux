using System.IO;
using RevitFlux.Core.Security;
using Xunit;

namespace RevitFlux.Tests.Security;

public class ScriptSettingsTamperTests
{
    [Theory]
    [InlineData("RevitFlux.Core.Security.CortexSettings.SetSkipScriptConfirmation(true);")]
    [InlineData("global::RevitFlux . Plugin.RevitFluxApp.Instance.StopService();")]
    [InlineData("using S = RevitFlux.Core.Security.CortexSettings; S.SetSkipScriptConfirmation(true);")]
    public void Script_CannotReachRevitFlux(string code) => Assert.NotNull(CodeSandbox.Validate(code));

    [Fact]
    public void Script_MayMentionRevitFluxInText() =>
        Assert.Null(CodeSandbox.Validate("// RevitFlux\nreturn \"RevitFlux\";"));

    [Fact]
    public void ConfirmationIsSkipped_OnlyAfterEnableAndSandboxGates()
    {
        var src = File.ReadAllText(Path.GetFullPath(Path.Combine(
            "..", "..", "..", "..", "RevitFlux.Tools", "Elements", "SendCodeToRevitTool.cs")));
        var enabled = src.IndexOf("!settings.EnableCodeExecution", System.StringComparison.Ordinal);
        var sandbox = src.IndexOf("CodeSandbox.Validate", System.StringComparison.Ordinal);
        var skip = src.IndexOf("settings.SkipScriptConfirmation", System.StringComparison.Ordinal);
        Assert.True(enabled > 0 && enabled < sandbox && sandbox < skip);
    }
}
