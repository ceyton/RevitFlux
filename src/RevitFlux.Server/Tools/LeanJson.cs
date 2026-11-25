using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitFlux.Server.Tools;

/// <summary>
/// Writes tool results with as few tokens as possible: no indentation, no float noise, and
/// repeated objects as one table ({"same":{...},"columns":[...],"rows":[[...]]}).
/// A result that is still too large is saved to disk; a preview and the path are returned.
/// </summary>
public static class LeanJson
{
    public const int MaxChars = 24000; // about 6-8k tokens

    private static readonly string ResultsFolder = Path.Combine(
        Environment.GetEnvironmentVariable("REVITFLUX_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".revitflux"),
        "results");

    public static string ToLean(this JToken token) => Write(token, MaxChars);

    public static string Write(JToken token, int maxChars)
    {
        var packed = Pack(token);
        var text = packed.ToString(Formatting.None);
        if (text.Length <= maxChars) return text;
        return Preview(packed, text, maxChars);
    }

    public static JToken Pack(JToken token)
    {
        switch (token)
        {
            case JObject o:
                return new JObject(o.Properties().Select(p => new JProperty(p.Name, Pack(p.Value))));
            case JArray a:
                return (JToken?)PackTable(a) ?? new JArray(a.Select(Pack));
            case JValue { Type: JTokenType.Float } v when v.Value is double d:
                // Revit returns e.g. 2999.9999999999995. Tiny values keep full precision.
                return Math.Abs(d) >= 0.001 && Math.Abs(d) < 1e9 ? new JValue(Math.Round(d, 6)) : v.DeepClone();
            default:
                return token.DeepClone();
        }
    }

    private static JObject? PackTable(JArray array)
    {
        if (array.Count < 3 || array.Any(t => t is not JObject)) return null;
        var rows = array.Cast<JObject>().ToList();
        var columns = rows[0].Properties().Select(p => p.Name).ToList();
        // Same keys everywhere, so a missing value can never be confused with null.
        if (columns.Count == 0 || rows.Any(r => r.Count != columns.Count || columns.Any(c => !r.ContainsKey(c))))
            return null;

        var same = new JObject();
        foreach (var c in columns)
        {
            if (rows.All(r => JToken.DeepEquals(r[c], rows[0][c]))) same[c] = Pack(rows[0][c]!);
        }
        var varying = columns.Where(c => !same.ContainsKey(c)).ToList();

        var table = new JObject();
        if (same.Count > 0) table["same"] = same;
        table["columns"] = new JArray(varying);
        table["rows"] = new JArray(rows.Select(r => new JArray(varying.Select(c => Pack(r[c]!)))));
        return table;
    }

    private static string Preview(JToken packed, string fullText, int maxChars)
    {
        string path;
        try
        {
            path = Save(fullText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return fullText; // losing data is worse than spending tokens
        }

        var root = packed as JObject ?? new JObject { ["items"] = packed };
        var totals = new Dictionary<string, int>();
        var text = fullText;
        while (text.Length > maxChars)
        {
            // Never shorten a "columns" list: rows would no longer line up with it.
            var biggest = root.DescendantsAndSelf().OfType<JArray>()
                .Where(a => a.Count > 1 && a.Parent is JProperty { Name: not "columns" })
                .OrderByDescending(a => a.ToString(Formatting.None).Length)
                .FirstOrDefault();
            if (biggest == null) break;
            if (!totals.ContainsKey(biggest.Path)) totals[biggest.Path] = biggest.Count;
            var keep = biggest.Count / 2;
            while (biggest.Count > keep) biggest.RemoveAt(biggest.Count - 1);
            text = root.ToString(Formatting.None);
        }

        var shown = new JObject();
        foreach (var pair in totals)
        {
            if (root.SelectToken(pair.Key) is JArray a) shown[pair.Key] = $"{a.Count} of {pair.Value}";
        }
        root["_preview"] = new JObject
        {
            ["shown"] = shown,
            ["fullResult"] = path,
            ["hint"] = "Incomplete. Narrow the query, or read/search the fullResult file. Do not count or sum from this preview."
        };
        return root.ToString(Formatting.None);
    }

    private static string Save(string text)
    {
        Directory.CreateDirectory(ResultsFolder);
        foreach (var old in Directory.GetFiles(ResultsFolder, "*.json"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1)) File.Delete(old);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var path = Path.Combine(ResultsFolder, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}.json");
        File.WriteAllText(path, text);
        return path;
    }
}
