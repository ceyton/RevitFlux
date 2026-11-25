using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace RevitFlux.Tests.Tools;

public class ToolCatalogParitySourceTests
{
    private static string ProjectPath(string project, params string[] relativeParts)
    {
        var parts = new List<string> { "..", "..", "..", "..", project };
        parts.AddRange(relativeParts);
        return Path.GetFullPath(Path.Combine(parts.ToArray()));
    }

    private static IEnumerable<string> ReadCsFiles(string project, params string[] relativeParts)
    {
        var root = ProjectPath(project, relativeParts);
        if (!Directory.Exists(root)) return Enumerable.Empty<string>();
        return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
    }

    private static HashSet<string> ExtractNames(IEnumerable<string> files, Regex pattern)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (Match match in pattern.Matches(source))
                names.Add(match.Groups[1].Value);
        }
        return names;
    }

    [Fact]
    public void EveryMcpServerTool_HasARegisteredPluginTool()
    {
        var mcpNames = ExtractNames(
            ReadCsFiles("RevitFlux.Server", "Tools"),
            new Regex(@"McpServerTool\(Name\s*=\s*""([^""]+)"""));

        var pluginFiles = ReadCsFiles("RevitFlux.Tools")
            .Concat(ReadCsFiles("RevitFlux.Plugin"));
        // Match the Name property in any common shape so a tool cannot silently
        // escape the parity check: expression-bodied (=> "x") or a property with
        // an initializer ({ get; } = "x" / { get; set; } = "x"). Over-matching
        // non-tool Name members is harmless — it only adds to the allowed set.
        var pluginNames = ExtractNames(
            pluginFiles,
            new Regex(@"public\s+string\s+Name\s*(?:=>\s*|\{[^}]*\}\s*=\s*)""([^""]+)"""));

        Assert.NotEmpty(mcpNames);
        Assert.NotEmpty(pluginNames);

        var missing = mcpNames
            .Where(name => name is not ("find_tools" or "run_tool")) // server-side only
            .Where(name => !pluginNames.Contains(name))
            .OrderBy(name => name)
            .ToList();

        Assert.True(missing.Count == 0,
            "MCP server tools without matching ICortexTool registry names: " +
            string.Join(", ", missing));
    }
}
