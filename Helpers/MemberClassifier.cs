using System;
using System.Text.RegularExpressions;

namespace SERhinoIFC.Helpers
{
    public static class MemberClassifier
    {
        /// <summary>
        /// Maps a Rhino layer name to an IFC element type name using keyword matching.
        /// </summary>
        public static string GetIfcTypeFromLayerName(string layerName)
        {
            if (string.IsNullOrEmpty(layerName))
                return "IfcBuildingElementProxy";

            string lower = layerName.ToLowerInvariant();

            if (lower.Contains("wall"))
                return "IfcWall";
            if (lower.Contains("slab") || lower.Contains("floor") || lower.Contains("ceiling"))
                return "IfcSlab";
            if (lower.Contains("column") || lower.Contains("post"))
                return "IfcColumn";
            if (lower.Contains("beam") || lower.Contains("girder"))
                return "IfcBeam";
            if (lower.Contains("roof"))
                return "IfcRoof";

            return "IfcBuildingElementProxy";
        }
    }
}
