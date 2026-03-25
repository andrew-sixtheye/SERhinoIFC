namespace SERhinoIFC.Export
{
    public class ExportOptions
    {
        public ExportMode Mode { get; set; }
        public ExportUnitSystem Units { get; set; } = ExportUnitSystem.Inches;
        public double Tolerance { get; set; } = 0.001;
        public string IfcSchema { get; set; } = "IFC2x3";
        public string Author { get; set; }
        public string Organization { get; set; }
    }

    public enum ExportMode
    {
        General,
        FrameCAD
    }

    public enum ExportUnitSystem
    {
        Millimeters,
        Centimeters,
        Meters,
        Inches,
        Feet
    }

    public static class ExportUnitHelper
    {
        public static Rhino.UnitSystem ToRhinoUnitSystem(ExportUnitSystem units)
        {
            switch (units)
            {
                case ExportUnitSystem.Millimeters: return Rhino.UnitSystem.Millimeters;
                case ExportUnitSystem.Centimeters: return Rhino.UnitSystem.Centimeters;
                case ExportUnitSystem.Meters:      return Rhino.UnitSystem.Meters;
                case ExportUnitSystem.Inches:      return Rhino.UnitSystem.Inches;
                case ExportUnitSystem.Feet:        return Rhino.UnitSystem.Feet;
                default:                           return Rhino.UnitSystem.Meters;
            }
        }

        public static void CreateLengthUnit(Xbim.Ifc.IfcStore model,
            Xbim.Ifc2x3.MeasureResource.IfcUnitAssignment unitAssignment, ExportUnitSystem units)
        {
            switch (units)
            {
                case ExportUnitSystem.Millimeters:
                    unitAssignment.Units.Add(model.Instances.New<Xbim.Ifc2x3.MeasureResource.IfcSIUnit>(u =>
                    {
                        u.UnitType = Xbim.Ifc2x3.MeasureResource.IfcUnitEnum.LENGTHUNIT;
                        u.Name = Xbim.Ifc2x3.MeasureResource.IfcSIUnitName.METRE;
                        u.Prefix = Xbim.Ifc2x3.MeasureResource.IfcSIPrefix.MILLI;
                    }));
                    break;
                case ExportUnitSystem.Centimeters:
                    unitAssignment.Units.Add(model.Instances.New<Xbim.Ifc2x3.MeasureResource.IfcSIUnit>(u =>
                    {
                        u.UnitType = Xbim.Ifc2x3.MeasureResource.IfcUnitEnum.LENGTHUNIT;
                        u.Name = Xbim.Ifc2x3.MeasureResource.IfcSIUnitName.METRE;
                        u.Prefix = Xbim.Ifc2x3.MeasureResource.IfcSIPrefix.CENTI;
                    }));
                    break;
                case ExportUnitSystem.Inches:
                case ExportUnitSystem.Feet:
                    // Conversion-based units for imperial
                    var mmUnit = model.Instances.New<Xbim.Ifc2x3.MeasureResource.IfcSIUnit>(u =>
                    {
                        u.UnitType = Xbim.Ifc2x3.MeasureResource.IfcUnitEnum.LENGTHUNIT;
                        u.Name = Xbim.Ifc2x3.MeasureResource.IfcSIUnitName.METRE;
                        u.Prefix = Xbim.Ifc2x3.MeasureResource.IfcSIPrefix.MILLI;
                    });
                    double convFactor = units == ExportUnitSystem.Inches ? 25.4 : 304.8;
                    string unitName = units == ExportUnitSystem.Inches ? "INCH" : "FOOT";

                    var dimExp = model.Instances.New<Xbim.Ifc2x3.MeasureResource.IfcDimensionalExponents>(de =>
                    {
                        de.LengthExponent = 1;
                    });
                    var measure = model.Instances.New<Xbim.Ifc2x3.MeasureResource.IfcMeasureWithUnit>(m =>
                    {
                        m.ValueComponent = new Xbim.Ifc2x3.MeasureResource.IfcLengthMeasure(convFactor);
                        m.UnitComponent = mmUnit;
                    });
                    unitAssignment.Units.Add(model.Instances.New<Xbim.Ifc2x3.MeasureResource.IfcConversionBasedUnit>(u =>
                    {
                        u.UnitType = Xbim.Ifc2x3.MeasureResource.IfcUnitEnum.LENGTHUNIT;
                        u.Name = unitName;
                        u.Dimensions = dimExp;
                        u.ConversionFactor = measure;
                    }));
                    break;
                default: // Meters
                    unitAssignment.Units.Add(model.Instances.New<Xbim.Ifc2x3.MeasureResource.IfcSIUnit>(u =>
                    {
                        u.UnitType = Xbim.Ifc2x3.MeasureResource.IfcUnitEnum.LENGTHUNIT;
                        u.Name = Xbim.Ifc2x3.MeasureResource.IfcSIUnitName.METRE;
                    }));
                    break;
            }
        }
    }
}
