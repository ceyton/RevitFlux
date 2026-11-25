using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitFlux.Core.Security;

namespace RevitFlux.Plugin.UI;

public sealed class ToolInfo
{
    public ToolInfo(string name, string category, string description, bool enabled)
    {
        Name = name; Category = category; Description = description; Enabled = enabled;
    }

    public string Name { get; }
    public string Category { get; }
    public string Description { get; }
    public bool Enabled { get; }
}

/// <summary>What the settings window needs from the add-in. Keeps the window free of Revit types.</summary>
public interface ISettingsHost
{
    bool IsRunning { get; }
    int Port { get; }
    string Version { get; }
    string RevitVersion { get; }
    bool IsDark { get; }
    event Action? StateChanged;
    void ToggleServer();
    IReadOnlyList<ToolInfo> GetTools();
    void ApplyDisabledTools(IReadOnlyCollection<string> names);
    void ApplyReadOnly(bool readOnly);
}

/// <summary>Reads and writes single keys of settings.json, leaving every other key alone.</summary>
public static class SettingsStore
{
    public static JObject Read()
    {
        var path = CortexSettings.DefaultPath;
        return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
    }

    public static void Write(params (string key, JToken value)[] values)
    {
        // A file that cannot be parsed throws here, so it is reported instead of overwritten.
        var root = Read();
        foreach (var (key, value) in values) root[key] = value;

        var path = CortexSettings.DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToString(Formatting.Indented));
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }
}
