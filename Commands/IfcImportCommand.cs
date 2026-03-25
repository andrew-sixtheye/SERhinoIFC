using System;
using Rhino;
using Rhino.Commands;
using SERhinoIFC.Dialogs;
using SERhinoIFC.Import;

namespace SERhinoIFC.Commands
{
    public class IfcImportCommand : Command
    {
        public override string EnglishName => "IfcImport";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var fileDialog = new Rhino.UI.OpenFileDialog
            {
                Filter = "IFC Files (*.ifc)|*.ifc",
                Title = "Select IFC File to Import"
            };

            if (!fileDialog.ShowOpenDialog())
                return Result.Cancel;

            string filePath = fileDialog.FileName;

            try
            {
                var importer = new IfcImporter();

                // Read IFC units before importing
                var detectedUnit = importer.ReadUnitInfo(filePath);

                // Show unit confirmation dialog
                var dialog = new ImportUnitsDialog(detectedUnit, doc.ModelUnitSystem);
                dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow);

                if (!dialog.Result)
                    return Result.Cancel;

                var selectedUnit = dialog.SelectedIfcUnit;
                int count = importer.Import(filePath, doc, selectedUnit);
                RhinoApp.WriteLine($"Imported {count} objects from {System.IO.Path.GetFileName(filePath)}");
                doc.Views.Redraw();
                return Result.Success;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"IFC Import failed: {ex.Message}");
                return Result.Failure;
            }
        }
    }
}
