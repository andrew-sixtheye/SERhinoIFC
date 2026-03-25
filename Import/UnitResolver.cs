using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Xbim.Common;
using Xbim.Ifc;
using Xbim.Ifc4.Interfaces;

namespace SERhinoIFC.Import
{
    public static class UnitResolver
    {
        /// <summary>
        /// Computes the scale factor to convert IFC coordinates to Rhino document units.
        /// Reads the declared length unit from the IFC file — never hardcoded.
        /// </summary>
        public static double GetScaleFactor(IModel model, RhinoDoc doc)
        {
            double ifcUnitInMeters = 1.0; // default: assume meters if not declared

            var project = model.Instances.OfType<IIfcProject>().FirstOrDefault();
            if (project?.UnitsInContext != null)
            {
                var lengthUnit = project.UnitsInContext.Units
                    .OfType<IIfcNamedUnit>()
                    .FirstOrDefault(u => u.UnitType == IfcUnitEnum.LENGTHUNIT);

                if (lengthUnit is IIfcSIUnit siUnit)
                {
                    ifcUnitInMeters = GetSIPrefixFactor(siUnit.Prefix);
                }
                else if (lengthUnit is IIfcConversionBasedUnit convUnit)
                {
                    var factor = convUnit.ConversionFactor;
                    if (factor?.ValueComponent != null)
                    {
                        ifcUnitInMeters = (double)(factor.ValueComponent.Value);
                    }
                }
                else
                {
                    RhinoApp.WriteLine("Warning: IFC file has no recognized length unit. Assuming meters.");
                }
            }
            else
            {
                RhinoApp.WriteLine("Warning: IFC file has no declared units. Assuming meters.");
            }

            // How many meters per one Rhino document unit
            double rhinoUnitInMeters = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);

            return ifcUnitInMeters / rhinoUnitInMeters;
        }

        private static double GetSIPrefixFactor(IfcSIPrefix? prefix)
        {
            if (prefix == null)
                return 1.0;

            switch (prefix.Value)
            {
                case IfcSIPrefix.MILLI: return 0.001;
                case IfcSIPrefix.CENTI: return 0.01;
                case IfcSIPrefix.DECI: return 0.1;
                case IfcSIPrefix.KILO: return 1000.0;
                default: return 1.0;
            }
        }
    }
}
