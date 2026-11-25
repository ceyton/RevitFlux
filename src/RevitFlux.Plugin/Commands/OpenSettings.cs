using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitFlux.Plugin.UI;

namespace RevitFlux.Plugin.Commands;

[Transaction(TransactionMode.Manual)]
public class OpenSettings : IExternalCommand
{
    private static SettingsWindow? _window;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var app = RevitFluxApp.Instance;
        if (app == null)
        {
            message = "RevitFlux is not initialized.";
            return Result.Failed;
        }
        if (_window != null)
        {
            if (_window.WindowState == System.Windows.WindowState.Minimized)
                _window.WindowState = System.Windows.WindowState.Normal;
            _window.Activate();
            return Result.Succeeded;
        }

        var host = new RevitSettingsHost(app, commandData.Application.Application.VersionNumber);
        _window = new SettingsWindow(host);
        _ = new System.Windows.Interop.WindowInteropHelper(_window)
        {
            Owner = commandData.Application.MainWindowHandle
        };
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        return Result.Succeeded;
    }
}
