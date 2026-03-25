using System;
using Rhino;
using Rhino.Commands;

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
                var importer = new Import.IfcImporter();
                int count = importer.Import(filePath, doc);
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
