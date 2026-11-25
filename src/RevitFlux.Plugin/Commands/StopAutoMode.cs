using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitFlux.Plugin.UI;
using System;

namespace RevitFlux.Plugin.Commands;

[Transaction(TransactionMode.ReadOnly)]
public class StopAutoMode : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var app = RevitFluxApp.Instance;
            if (app?.Session == null) return Result.Succeeded;

            app.Session.AutoMode = false;
            ConfirmationHelper.NotifyAutoModeChanged(false);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}
