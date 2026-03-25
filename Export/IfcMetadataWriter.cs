using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Rhino;
using Rhino.DocObjects;
using Xbim.Ifc;
using Xbim.Ifc2x3.Kernel;
using Xbim.Ifc2x3.MaterialResource;
using Xbim.Ifc2x3.MeasureResource;
using Xbim.Ifc2x3.ProductExtension;
using Xbim.Ifc2x3.PropertyResource;
using Xbim.Ifc2x3.QuantityResource;
using Xbim.Ifc2x3.SharedBldgElements;
using Xbim.Ifc2x3.StructuralElementsDomain;

namespace SERhinoIFC.Export
{
    /// <summary>
    /// Reads Rhino user text attributes and writes them back to IFC entities,
    /// enabling full metadata roundtrip.
    /// </summary>
    public class IfcMetadataWriter
    {
        private readonly Dictionary<string, IfcMaterial> _materialCache = new Dictionary<string, IfcMaterial>();

        private static readonly Regex QuantityPattern =
            new Regex(@"^([\d.eE+-]+)\s*\[(length|area|volume|weight|count|time)\]$");

        /// <summary>
        /// Creates the correct IFC element type based on user text "IfcType" or layer name fallback.
        /// </summary>
        public IfcProduct CreateElement(IfcStore model, RhinoObject rhinoObj, RhinoDoc doc)
        {
            string ifcType = rhinoObj.Attributes.GetUserString("IfcType");

            // Fallback to layer name
            if (string.IsNullOrEmpty(ifcType))
            {
                var layer = doc.Layers[rhinoObj.Attributes.LayerIndex];
                ifcType = Helpers.MemberClassifier.GetIfcTypeFromLayerName(layer.FullPath);
            }

            string name = rhinoObj.Attributes.GetUserString("Name")
                ?? rhinoObj.Name
                ?? ifcType;

            IfcProduct element;
            switch (ifcType)
            {
                case "IfcWall":
                    element = model.Instances.New<IfcWall>(e => e.Name = name);
                    break;
                case "IfcSlab":
                    element = model.Instances.New<IfcSlab>(e => e.Name = name);
                    break;
                case "IfcColumn":
                    element = model.Instances.New<IfcColumn>(e => e.Name = name);
                    break;
                case "IfcBeam":
                    element = model.Instances.New<IfcBeam>(e => e.Name = name);
                    break;
                case "IfcMember":
                    element = model.Instances.New<IfcMember>(e => e.Name = name);
                    break;
                case "IfcRoof":
                    element = model.Instances.New<IfcRoof>(e => e.Name = name);
                    break;
                case "IfcPlate":
                    element = model.Instances.New<IfcPlate>(e => e.Name = name);
                    break;
                case "IfcFooting":
                    element = model.Instances.New<IfcFooting>(e => e.Name = name);
                    break;
                case "IfcRailing":
                    element = model.Instances.New<IfcRailing>(e => e.Name = name);
                    break;
                case "IfcStair":
                    element = model.Instances.New<IfcStair>(e => e.Name = name);
                    break;
                case "IfcDoor":
                    element = model.Instances.New<IfcDoor>(e => e.Name = name);
                    break;
                case "IfcWindow":
                    element = model.Instances.New<IfcWindow>(e => e.Name = name);
                    break;
                case "IfcCovering":
                    element = model.Instances.New<IfcCovering>(e => e.Name = name);
                    break;
                case "IfcCurtainWall":
                    element = model.Instances.New<IfcCurtainWall>(e => e.Name = name);
                    break;
                case "IfcPile":
                    element = model.Instances.New<IfcPile>(e => e.Name = name);
                    break;
                default:
                    element = model.Instances.New<IfcBuildingElementProxy>(e => e.Name = name);
                    break;
            }

            return element;
        }

        /// <summary>
        /// Writes all metadata from Rhino user text onto the IFC element.
        /// </summary>
        public void ApplyMetadata(IfcStore model, IfcProduct element, RhinoObject rhinoObj)
        {
            var attrs = rhinoObj.Attributes;

            // Standard attributes
            string globalId = attrs.GetUserString("GlobalId");
            if (!string.IsNullOrEmpty(globalId))
                element.GlobalId = globalId;

            string description = attrs.GetUserString("Description");
            if (!string.IsNullOrEmpty(description))
                element.Description = description;

            string objectType = attrs.GetUserString("ObjectType");
            if (!string.IsNullOrEmpty(objectType) && element is IfcObject obj)
                obj.ObjectType = objectType;

            string tag = attrs.GetUserString("Tag");
            if (!string.IsNullOrEmpty(tag) && element is IfcElement elem)
                elem.Tag = tag;

            // Collect all user strings and group by prefix
            var allKeys = attrs.GetUserStrings().AllKeys;
            var psetGroups = new Dictionary<string, List<(string propName, string value)>>();
            var qsetGroups = new Dictionary<string, List<(string qtyName, string value)>>();

            // Reserved keys that are handled separately
            var reservedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "IfcType", "GlobalId", "Name", "Description", "ObjectType",
                "Tag", "PredefinedType", "TypeName"
            };

            foreach (var key in allKeys)
            {
                if (reservedKeys.Contains(key)) continue;
                if (key.StartsWith("Material.")) continue; // handled separately

                int dotIndex = key.IndexOf('.');
                if (dotIndex <= 0) continue;

                string prefix = key.Substring(0, dotIndex);
                string propName = key.Substring(dotIndex + 1);
                string value = attrs.GetUserString(key);

                if (string.IsNullOrEmpty(value)) continue;

                // Determine if this is a quantity set or property set
                if (QuantityPattern.IsMatch(value))
                {
                    if (!qsetGroups.ContainsKey(prefix))
                        qsetGroups[prefix] = new List<(string, string)>();
                    qsetGroups[prefix].Add((propName, value));
                }
                else
                {
                    if (!psetGroups.ContainsKey(prefix))
                        psetGroups[prefix] = new List<(string, string)>();
                    psetGroups[prefix].Add((propName, value));
                }
            }

            // Write property sets
            foreach (var kvp in psetGroups)
            {
                var pset = model.Instances.New<IfcPropertySet>(ps =>
                {
                    ps.Name = kvp.Key;
                });

                foreach (var (propName, value) in kvp.Value)
                {
                    pset.HasProperties.Add(model.Instances.New<IfcPropertySingleValue>(pv =>
                    {
                        pv.Name = propName;
                        // Try to parse as number, otherwise store as text
                        if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double numVal))
                        {
                            pv.NominalValue = new IfcReal(numVal);
                        }
                        else
                        {
                            // Strip unit suffix if present for property values
                            string cleanVal = Regex.Replace(value, @"\s*\[.*?\]\s*$", "").Trim();
                            pv.NominalValue = new IfcText(cleanVal);
                        }
                    }));
                }

                model.Instances.New<IfcRelDefinesByProperties>(rd =>
                {
                    rd.RelatingPropertyDefinition = pset;
                    rd.RelatedObjects.Add(element);
                });
            }

            // Write quantity sets
            foreach (var kvp in qsetGroups)
            {
                var qset = model.Instances.New<IfcElementQuantity>(eq =>
                {
                    eq.Name = kvp.Key;
                });

                foreach (var (qtyName, value) in kvp.Value)
                {
                    var match = QuantityPattern.Match(value);
                    if (!match.Success) continue;

                    double numVal = double.Parse(match.Groups[1].Value,
                        System.Globalization.CultureInfo.InvariantCulture);
                    string unitType = match.Groups[2].Value;

                    switch (unitType)
                    {
                        case "length":
                            qset.Quantities.Add(model.Instances.New<IfcQuantityLength>(q =>
                            {
                                q.Name = qtyName;
                                q.LengthValue = numVal;
                            }));
                            break;
                        case "area":
                            qset.Quantities.Add(model.Instances.New<IfcQuantityArea>(q =>
                            {
                                q.Name = qtyName;
                                q.AreaValue = numVal;
                            }));
                            break;
                        case "volume":
                            qset.Quantities.Add(model.Instances.New<IfcQuantityVolume>(q =>
                            {
                                q.Name = qtyName;
                                q.VolumeValue = numVal;
                            }));
                            break;
                        case "weight":
                            qset.Quantities.Add(model.Instances.New<IfcQuantityWeight>(q =>
                            {
                                q.Name = qtyName;
                                q.WeightValue = numVal;
                            }));
                            break;
                        case "count":
                            qset.Quantities.Add(model.Instances.New<IfcQuantityCount>(q =>
                            {
                                q.Name = qtyName;
                                q.CountValue = numVal;
                            }));
                            break;
                        case "time":
                            qset.Quantities.Add(model.Instances.New<IfcQuantityTime>(q =>
                            {
                                q.Name = qtyName;
                                q.TimeValue = numVal;
                            }));
                            break;
                    }
                }

                if (qset.Quantities.Count > 0)
                {
                    model.Instances.New<IfcRelDefinesByProperties>(rd =>
                    {
                        rd.RelatingPropertyDefinition = qset;
                        rd.RelatedObjects.Add(element);
                    });
                }
            }

            // Material
            string materialName = attrs.GetUserString("Material.Name");
            if (!string.IsNullOrEmpty(materialName))
            {
                IfcMaterial material;
                if (!_materialCache.TryGetValue(materialName, out material))
                {
                    material = model.Instances.New<IfcMaterial>(m => m.Name = materialName);
                    _materialCache[materialName] = material;
                }

                model.Instances.New<IfcRelAssociatesMaterial>(ram =>
                {
                    ram.RelatingMaterial = material;
                    ram.RelatedObjects.Add(element);
                });
            }
        }

        /// <summary>
        /// Gets the storey name from the Rhino layer hierarchy (top-level layer).
        /// </summary>
        public static string GetStoreyName(RhinoObject rhinoObj, RhinoDoc doc)
        {
            var layer = doc.Layers[rhinoObj.Attributes.LayerIndex];
            var current = layer;
            while (current.ParentLayerId != Guid.Empty)
            {
                int parentIndex = doc.Layers.FindId(current.ParentLayerId).Index;
                if (parentIndex < 0) break;
                current = doc.Layers[parentIndex];
            }
            return current.Name;
        }
    }
}
