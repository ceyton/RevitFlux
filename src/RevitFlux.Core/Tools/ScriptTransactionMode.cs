namespace RevitFlux.Core.Tools;

public enum ScriptTransactionMode { Auto, Manual, Group, ReadOnly }

public static class ScriptTransactionModes
{
    /// <summary>Parses a mode. Unknown values are rejected: falling back to a writable
    /// transaction would be unsafe.</summary>
    public static bool TryParse(string? value, out ScriptTransactionMode mode)
    {
        mode = ScriptTransactionMode.Auto;
        switch ((value ?? "auto").Trim().ToLowerInvariant())
        {
            case "auto": return true;
            case "manual":
            case "none": mode = ScriptTransactionMode.Manual; return true;
            case "group": mode = ScriptTransactionMode.Group; return true;
            case "readonly": mode = ScriptTransactionMode.ReadOnly; return true;
            default: return false;
        }
    }
}
