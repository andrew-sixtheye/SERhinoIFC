using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Xbim.Common;
using Xbim.Ifc;
using Xbim.Ifc4.Interfaces;

namespace SERhinoIFC.Import
{
    public class IfcUnitInfo
    {
        public string UnitName { get; set; }
        public double MetersPerUnit { get; set; }

        public string DisplayName
        {
            get
            {
                // Map common meter-per-unit values to friendly names
                if (MetersPerUnit == 1.0) return "Meters";
                if (MetersPerUnit == 0.001) return "Millimeters";
                if (MetersPerUnit == 0.01) return "Centimeters";
                if (MetersPerUnit == 0.3048) return "Feet";
                if (System.Math.Abs(MetersPerUnit - 0.0254) < 1e-8) return "Inches";
                if (System.Math.Abs(MetersPerUnit - 0.9144) < 1e-6) return "Yards";
                if (MetersPerUnit == 1000.0) return "Kilometers";
                return UnitName ?? $"{MetersPerUnit} m/unit";
            }
        }
    }

    public static class UnitResolver
    {
        /// <summary>
        /// Reads the declared length unit from the IFC file and returns structured info.
        /// </summary>
        public static IfcUnitInfo GetIfcUnitInfo(IModel model)
        {
            var info = new IfcUnitInfo { UnitName = "Meters", MetersPerUnit = 1.0 };

            var project = model.Instances.OfType<IIfcProject>().FirstOrDefault();
            if (project?.UnitsInContext == null)
            {
                RhinoApp.WriteLine("SERhinoIFC: IFC file has no declared units. Assuming meters.");
                return info;
            }

            var lengthUnit = project.UnitsInContext.Units
                .OfType<IIfcNamedUnit>()
                .FirstOrDefault(u => u.UnitType == IfcUnitEnum.LENGTHUNIT);

            if (lengthUnit is IIfcSIUnit siUnit)
            {
                info.MetersPerUnit = GetSIPrefixFactor(siUnit.Prefix);
                info.UnitName = siUnit.Prefix.HasValue ? $"{siUnit.Prefix.Value} {siUnit.Name}" : siUnit.Name.ToString();
            }
            else if (lengthUnit is IIfcConversionBasedUnit convUnit)
            {
                var factor = convUnit.ConversionFactor;
                if (factor?.ValueComponent != null)
                {
                    double convValue = (double)(factor.ValueComponent.Value);

                    double refUnitInMeters = 1.0;
                    if (factor.UnitComponent is IIfcSIUnit refSiUnit)
                    {
                        refUnitInMeters = GetSIPrefixFactor(refSiUnit.Prefix);
                    }

                    info.MetersPerUnit = convValue * refUnitInMeters;
                }
                info.UnitName = convUnit.Name.ToString();
            }
            else
            {
                RhinoApp.WriteLine("SERhinoIFC: IFC file has no recognized length unit. Assuming meters.");
            }

            return info;
        }

        /// <summary>
        /// Computes the scale factor to convert IFC coordinates to Rhino document units.
        /// </summary>
        public static double GetScaleFactor(IModel model, RhinoDoc doc)
        {
            var info = GetIfcUnitInfo(model);
            return ComputeScaleFactor(info, doc);
        }

        /// <summary>
        /// Computes scale factor from unit info and Rhino doc.
        /// </summary>
        public static double ComputeScaleFactor(IfcUnitInfo ifcUnit, RhinoDoc doc)
        {
            double rhinoUnitInMeters = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);
            double scale = ifcUnit.MetersPerUnit / rhinoUnitInMeters;

            RhinoApp.WriteLine($"SERhinoIFC: IFC unit = {ifcUnit.DisplayName} ({ifcUnit.MetersPerUnit} m/unit)");
            RhinoApp.WriteLine($"SERhinoIFC: Rhino unit = {doc.ModelUnitSystem} ({rhinoUnitInMeters} m/unit), scale = {scale}");

            return scale;
        }

        private static double GetSIPrefixFactor(IfcSIPrefix? prefix)
        {
            if (prefix == null)
                return 1.0;

            switch (prefix.Value)
            {
                case IfcSIPrefix.EXA:   return 1e18;
                case IfcSIPrefix.PETA:  return 1e15;
                case IfcSIPrefix.TERA:  return 1e12;
                case IfcSIPrefix.GIGA:  return 1e9;
                case IfcSIPrefix.MEGA:  return 1e6;
                case IfcSIPrefix.KILO:  return 1e3;
                case IfcSIPrefix.HECTO: return 1e2;
                case IfcSIPrefix.DECA:  return 1e1;
                case IfcSIPrefix.DECI:  return 1e-1;
                case IfcSIPrefix.CENTI: return 1e-2;
                case IfcSIPrefix.MILLI: return 1e-3;
                case IfcSIPrefix.MICRO: return 1e-6;
                case IfcSIPrefix.NANO:  return 1e-9;
                case IfcSIPrefix.PICO:  return 1e-12;
                case IfcSIPrefix.FEMTO: return 1e-15;
                case IfcSIPrefix.ATTO:  return 1e-18;
                default: return 1.0;
            }
        }
    }
}
