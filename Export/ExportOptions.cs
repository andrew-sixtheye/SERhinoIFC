namespace SERhinoIFC.Export
{
    public class ExportOptions
    {
        public ExportMode Mode { get; set; }
        public string IfcSchema { get; set; } = "IFC2x3";
        public string Author { get; set; }
        public string Organization { get; set; }
    }

    public enum ExportMode
    {
        General,
        FrameCAD
    }
}
