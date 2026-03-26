using System;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using SERhinoIFC.Dialogs;
using SERhinoIFC.Export;

namespace SERhinoIFC.Commands
{
    public class IfcExportCommand : Command
    {
        public override string EnglishName => "SEIfcExport";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var selectedObjects = doc.Objects.GetSelectedObjects(false, false).ToArray();
            if (selectedObjects.Length == 0)
            {
                RhinoApp.WriteLine("Select objects first.");
                return Result.Nothing;
            }

            // Show export options dialog
            var dialog = new ExportOptionsDialog(doc.ModelUnitSystem, doc.ModelAbsoluteTolerance);
            var options = dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow);
            if (options == null)
                return Result.Cancel;

            // Show save file dialog
            var saveDialog = new Rhino.UI.SaveFileDialog
            {
                Filter = "IFC Files (*.ifc)|*.ifc",
                Title = "Save IFC File"
            };

            if (!saveDialog.ShowSaveDialog())
                return Result.Cancel;

            string filePath = saveDialog.FileName;

            try
            {
                int count;
                if (options.Mode == ExportMode.General)
                {
                    var exporter = new GeneralExporter();
                    count = exporter.Export(selectedObjects, filePath, options, doc);
                }
                else
                {
                    var exporter = new FrameCADExporter();
                    count = exporter.Export(selectedObjects, filePath, options, doc);
                }

                RhinoApp.WriteLine($"Exported {count} objects to {System.IO.Path.GetFileName(filePath)}");
                return Result.Success;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"IFC Export failed: {ex.Message}");
                return Result.Failure;
            }
        }
    }
}
