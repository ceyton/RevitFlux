using System.Runtime.CompilerServices;

namespace RevitFlux.Tests;

internal static class TestHome
{
    /// <summary>Keeps tests out of the user's real audit log and settings.</summary>
    [ModuleInitializer]
    internal static void Init() => Environment.SetEnvironmentVariable("REVITFLUX_HOME",
        Path.Combine(Path.GetTempPath(), "revitflux-tests-" + Guid.NewGuid().ToString("N")));
}
