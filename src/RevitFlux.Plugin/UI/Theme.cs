using System;
using System.Windows;

namespace RevitFlux.Plugin.UI;

/// <summary>Merges the theme into one window. Application resources belong to Revit and stay untouched.</summary>
internal static class Theme
{
    public static void Apply(FrameworkElement root, bool dark)
    {
        var name = SystemParameters.HighContrast ? "HighContrast" : dark ? "Dark" : "Light";
        root.Resources.MergedDictionaries.Clear();
        root.Resources.MergedDictionaries.Add(Load(name));
        root.Resources.MergedDictionaries.Add(Load("Controls"));
    }

    private static ResourceDictionary Load(string name)
    {
        var assembly = typeof(Theme).Assembly.GetName().Name;
        return new ResourceDictionary
        {
            Source = new Uri($"/{assembly};component/UI/Themes/{name}.xaml", UriKind.Relative)
        };
    }
}
