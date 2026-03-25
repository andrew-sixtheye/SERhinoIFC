using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xbim.Common;
using Xbim.Common.Geometry;
using Xbim.Common.XbimExtensions;
using Xbim.Ifc;
using Xbim.Ifc4.Interfaces;
using Xbim.ModelGeometry.Scene;

namespace SERhinoIFC.Import
{
    public class IfcImporter
    {
        /// <summary>
        /// Reads the IFC file's declared length unit without running a full import.
        /// </summary>
        public IfcUnitInfo ReadUnitInfo(string filePath)
        {
            using (var model = IfcStore.Open(filePath))
            {
                return UnitResolver.GetIfcUnitInfo(model);
            }
        }

        public int Import(string filePath, RhinoDoc doc, IfcUnitInfo ifcUnitOverride = null)
        {
            int objectCount = 0;

            using (var model = IfcStore.Open(filePath))
            {
                double scaleFactor;
                if (ifcUnitOverride != null)
                    scaleFactor = UnitResolver.ComputeScaleFactor(ifcUnitOverride, doc);
                else
                    scaleFactor = UnitResolver.GetScaleFactor(model, doc);

                // Build lookup: element -> storey name
                var storeyLookup = BuildStoreyLookup(model);

                // Attempt xBIM geometry engine tessellation
                objectCount = ImportViaTessellation(model, doc, scaleFactor, storeyLookup);

                // Fallback: if tessellation produced nothing, read geometry directly
                if (objectCount == 0)
                {
                    RhinoApp.WriteLine("SERhinoIFC: xBIM tessellation returned 0 shapes. Attempting direct geometry fallback...");
                    objectCount = ImportDirectFallback(model, doc, scaleFactor, storeyLookup);

                    if (objectCount == 0)
                        RhinoApp.WriteLine("SERhinoIFC: Direct geometry fallback also produced 0 objects.");
                }
            }

            return objectCount;
        }

        private int ImportViaTessellation(IModel model, RhinoDoc doc, double scaleFactor,
            Dictionary<int, string> storeyLookup)
        {
            int objectCount = 0;

            // Tessellate all geometry
            Xbim3DModelContext context;
            try
            {
                context = new Xbim3DModelContext(model);
                context.CreateContext();
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"SERhinoIFC: Geometry context creation failed: {ex.Message}");
                RhinoApp.WriteLine($"SERhinoIFC: Inner exception: {ex.InnerException?.Message}");
                return 0;
            }

            IEnumerable<XbimShapeInstance> shapeInstances;
            try
            {
                shapeInstances = context.ShapeInstances();
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"SERhinoIFC: Failed to retrieve shape instances: {ex.Message}");
                return 0;
            }

            int totalShapes = 0;
            int skippedRepType = 0;
            int skippedNullGeom = 0;
            int skippedNullMesh = 0;
            int skippedNullElement = 0;

            foreach (var shapeInstance in shapeInstances)
            {
                totalShapes++;

                if (shapeInstance.RepresentationType != XbimGeometryRepresentationType.OpeningsAndAdditionsExcluded
                    && shapeInstance.RepresentationType != XbimGeometryRepresentationType.OpeningsAndAdditionsIncluded)
                {
                    skippedRepType++;
                    continue;
                }

                IXbimShapeGeometryData shapeGeometry;
                try
                {
                    shapeGeometry = context.ShapeGeometry(shapeInstance);
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"SERhinoIFC: ShapeGeometry failed for instance {shapeInstance.InstanceLabel}: {ex.Message}");
                    continue;
                }

                if (shapeGeometry == null)
                {
                    skippedNullGeom++;
                    continue;
                }

                Mesh rhinoMesh;
                try
                {
                    rhinoMesh = ConvertToRhinoMesh(shapeGeometry, shapeInstance.Transformation, scaleFactor);
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"SERhinoIFC: Mesh conversion failed for instance {shapeInstance.InstanceLabel}: {ex.Message}");
                    continue;
                }

                if (rhinoMesh == null || rhinoMesh.Vertices.Count == 0)
                {
                    skippedNullMesh++;
                    continue;
                }

                var element = model.Instances[shapeInstance.IfcProductLabel] as IIfcProduct;
                if (element == null)
                {
                    skippedNullElement++;
                    continue;
                }

                objectCount += AddMeshToDoc(doc, rhinoMesh, element, storeyLookup);
            }

            RhinoApp.WriteLine($"SERhinoIFC: Tessellation summary - {totalShapes} total shapes, " +
                $"{skippedRepType} filtered by rep type, {skippedNullGeom} null geometry, " +
                $"{skippedNullMesh} null/empty mesh, {skippedNullElement} null element, " +
                $"{objectCount} imported.");

            return objectCount;
        }

        private int ImportDirectFallback(IModel model, RhinoDoc doc, double scaleFactor,
            Dictionary<int, string> storeyLookup)
        {
            int objectCount = 0;

            // Fallback 1: IfcFacetedBrep - read triangulated faces directly
            var facetedBreps = model.Instances.OfType<IIfcFacetedBrep>().ToList();
            RhinoApp.WriteLine($"SERhinoIFC: Found {facetedBreps.Count} IIfcFacetedBrep entities for fallback.");
            foreach (var brep in facetedBreps)
            {
                try
                {
                    var mesh = ConvertFacetedBrep(brep, scaleFactor);
                    if (mesh == null || mesh.Vertices.Count == 0)
                    {
                        RhinoApp.WriteLine($"SERhinoIFC: FacetedBrep #{brep.EntityLabel} produced null/empty mesh.");
                        continue;
                    }

                    var product = FindOwningProduct(model, brep);
                    if (product != null)
                    {
                        objectCount += AddMeshToDoc(doc, mesh, product, storeyLookup);
                    }
                    else
                    {
                        // Ownership lookup failed — add geometry with a placeholder name
                        RhinoApp.WriteLine($"SERhinoIFC: FacetedBrep #{brep.EntityLabel} has no owning product. Adding as orphan geometry.");
                        objectCount += AddOrphanMeshToDoc(doc, mesh, $"FacetedBrep_{brep.EntityLabel}", storeyLookup);
                    }
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"SERhinoIFC: FacetedBrep fallback failed for #{brep.EntityLabel}: {ex.Message}");
                }
            }

            // Fallback 2: IfcExtrudedAreaSolid - build extrusion geometry
            var extrusions = model.Instances.OfType<IIfcExtrudedAreaSolid>().ToList();
            RhinoApp.WriteLine($"SERhinoIFC: Found {extrusions.Count} IIfcExtrudedAreaSolid entities for fallback.");
            foreach (var extrusion in extrusions)
            {
                try
                {
                    var mesh = ConvertExtrudedAreaSolid(extrusion, scaleFactor);
                    if (mesh == null || mesh.Vertices.Count == 0)
                    {
                        RhinoApp.WriteLine($"SERhinoIFC: ExtrudedAreaSolid #{extrusion.EntityLabel} produced null/empty mesh.");
                        continue;
                    }

                    var product = FindOwningProduct(model, extrusion);
                    if (product != null)
                    {
                        objectCount += AddMeshToDoc(doc, mesh, product, storeyLookup);
                    }
                    else
                    {
                        RhinoApp.WriteLine($"SERhinoIFC: ExtrudedAreaSolid #{extrusion.EntityLabel} has no owning product. Adding as orphan geometry.");
                        objectCount += AddOrphanMeshToDoc(doc, mesh, $"Extrusion_{extrusion.EntityLabel}", storeyLookup);
                    }
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"SERhinoIFC: ExtrudedAreaSolid fallback failed for #{extrusion.EntityLabel}: {ex.Message}");
                }
            }

            RhinoApp.WriteLine($"SERhinoIFC: Direct fallback imported {objectCount} objects.");
            return objectCount;
        }

        private Mesh ConvertFacetedBrep(IIfcFacetedBrep brep, double scaleFactor)
        {
            var rhinoMesh = new Mesh();
            var shell = brep.Outer;
            if (shell == null) return null;

            // Map IFC cartesian points to vertex indices
            var pointIndexMap = new Dictionary<int, int>();

            foreach (var face in shell.CfsFaces)
            {
                foreach (var bound in face.Bounds)
                {
                    var loop = bound.Bound as IIfcPolyLoop;
                    if (loop == null) continue;

                    var polygon = loop.Polygon.ToList();
                    if (polygon.Count < 3) continue;

                    // Ensure all vertices are added
                    var faceIndices = new List<int>();
                    foreach (var pt in polygon)
                    {
                        int label = pt.EntityLabel;
                        if (!pointIndexMap.ContainsKey(label))
                        {
                            var coords = pt.Coordinates.ToArray();
                            double x = coords.Length > 0 ? coords[0] : 0;
                            double y = coords.Length > 1 ? coords[1] : 0;
                            double z = coords.Length > 2 ? coords[2] : 0;
                            pointIndexMap[label] = rhinoMesh.Vertices.Count;
                            rhinoMesh.Vertices.Add(x * scaleFactor, y * scaleFactor, z * scaleFactor);
                        }
                        faceIndices.Add(pointIndexMap[label]);
                    }

                    // Triangulate the polygon using fan triangulation
                    for (int i = 1; i < faceIndices.Count - 1; i++)
                    {
                        rhinoMesh.Faces.AddFace(faceIndices[0], faceIndices[i], faceIndices[i + 1]);
                    }
                }
            }

            if (rhinoMesh.Vertices.Count == 0) return null;

            rhinoMesh.Normals.ComputeNormals();
            rhinoMesh.Compact();
            return rhinoMesh;
        }

        private Mesh ConvertExtrudedAreaSolid(IIfcExtrudedAreaSolid extrusion, double scaleFactor)
        {
            // Get the profile
            var profile = extrusion.SweptArea;
            var polyline = ExtractProfilePolyline(profile, scaleFactor);
            if (polyline == null || polyline.Count < 3)
                return null;

            // Get extrusion direction and depth
            var dir = extrusion.ExtrudedDirection;
            double depth = extrusion.Depth;
            var extrudeVec = new Vector3d(
                dir.DirectionRatios[0] * depth * scaleFactor,
                dir.DirectionRatios[1] * depth * scaleFactor,
                dir.DirectionRatios[2] * depth * scaleFactor);

            // Apply the local placement transform
            Transform placement = GetPlacementTransform(extrusion.Position, scaleFactor);

            // Build mesh: bottom face, top face, side faces
            var mesh = new Mesh();
            int n = polyline.Count;

            // Bottom vertices (profile)
            for (int i = 0; i < n; i++)
                mesh.Vertices.Add(polyline[i]);

            // Top vertices (profile + extrusion)
            for (int i = 0; i < n; i++)
                mesh.Vertices.Add(polyline[i] + extrudeVec);

            // Bottom face (fan triangulation, reversed winding)
            for (int i = 1; i < n - 1; i++)
                mesh.Faces.AddFace(0, i + 1, i);

            // Top face (fan triangulation)
            for (int i = 1; i < n - 1; i++)
                mesh.Faces.AddFace(n, n + i, n + i + 1);

            // Side faces
            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                mesh.Faces.AddFace(i, next, next + n, i + n);
            }

            mesh.Transform(placement);
            mesh.Normals.ComputeNormals();
            mesh.Compact();
            return mesh;
        }

        private List<Point3d> ExtractProfilePolyline(IIfcProfileDef profile, double scaleFactor)
        {
            // Handle IfcArbitraryClosedProfileDef (polyline outer curve)
            var arbProfile = profile as IIfcArbitraryClosedProfileDef;
            if (arbProfile != null)
            {
                var polyline = arbProfile.OuterCurve as IIfcPolyline;
                if (polyline != null)
                {
                    var points = new List<Point3d>();
                    foreach (var pt in polyline.Points)
                    {
                        var coords = pt.Coordinates.ToArray();
                        double x = coords.Length > 0 ? coords[0] * scaleFactor : 0;
                        double y = coords.Length > 1 ? coords[1] * scaleFactor : 0;
                        points.Add(new Point3d(x, y, 0));
                    }
                    // Remove duplicate closing point if present
                    if (points.Count > 1 && points[0].DistanceTo(points[points.Count - 1]) < 1e-6)
                        points.RemoveAt(points.Count - 1);
                    return points;
                }
            }

            // Handle IfcRectangleProfileDef
            var rectProfile = profile as IIfcRectangleProfileDef;
            if (rectProfile != null)
            {
                double xDim = rectProfile.XDim * scaleFactor;
                double yDim = rectProfile.YDim * scaleFactor;
                double halfX = xDim / 2.0;
                double halfY = yDim / 2.0;
                return new List<Point3d>
                {
                    new Point3d(-halfX, -halfY, 0),
                    new Point3d( halfX, -halfY, 0),
                    new Point3d( halfX,  halfY, 0),
                    new Point3d(-halfX,  halfY, 0)
                };
            }

            // Handle IfcCircleProfileDef (approximate as polygon)
            var circProfile = profile as IIfcCircleProfileDef;
            if (circProfile != null)
            {
                double radius = circProfile.Radius * scaleFactor;
                int segments = 24;
                var points = new List<Point3d>();
                for (int i = 0; i < segments; i++)
                {
                    double angle = 2.0 * Math.PI * i / segments;
                    points.Add(new Point3d(radius * Math.Cos(angle), radius * Math.Sin(angle), 0));
                }
                return points;
            }

            // Handle IfcCShapeProfileDef (cold-formed steel C-channel)
            var cProfile = profile as IIfcCShapeProfileDef;
            if (cProfile != null)
            {
                double d = cProfile.Depth * scaleFactor;      // web height
                double w = cProfile.Width * scaleFactor;       // flange width
                double t = cProfile.WallThickness * scaleFactor;
                double g = cProfile.Girth * scaleFactor;       // lip length

                double halfD = d / 2.0;

                // Outer perimeter CCW starting at bottom-right of web, going up
                // The C-shape opens to the left: web on the right, flanges extend left, lips turn inward
                //
                //   (5)───(4)          (3)───(2)
                //    |                        |
                //   (6)    (9)        (10)   (1)
                //           |          |
                //          (8)──────(11)
                //           |          |
                //          (7)    (12) (0)
                //    |                        |
                //  (15)  (14)        (13)  (23=0 wrap)
                //
                // Points traced as outer boundary, then inner boundary

                var points = new List<Point3d>
                {
                    // Outer path (CCW from bottom-right)
                    new Point3d(0,      -halfD,         0),  // 0: bottom-right corner (web base)
                    new Point3d(0,       halfD,         0),  // 1: top-right corner (web top)
                    new Point3d(-w,      halfD,         0),  // 2: top flange end
                    new Point3d(-w,      halfD - g,     0),  // 3: top lip end
                    new Point3d(-w + t,  halfD - g,     0),  // 4: top lip inner
                    new Point3d(-w + t,  halfD - t,     0),  // 5: top flange inner
                    new Point3d(-t,      halfD - t,     0),  // 6: web inner top
                    new Point3d(-t,     -halfD + t,     0),  // 7: web inner bottom
                    new Point3d(-w + t, -halfD + t,     0),  // 8: bottom flange inner
                    new Point3d(-w + t, -halfD + g,     0),  // 9: bottom lip inner
                    new Point3d(-w,     -halfD + g,     0),  // 10: bottom lip end
                    new Point3d(-w,     -halfD,         0),  // 11: bottom flange end
                };

                return points;
            }

            // Handle IfcUShapeProfileDef
            var uProfile = profile as IIfcUShapeProfileDef;
            if (uProfile != null)
            {
                double d = uProfile.Depth * scaleFactor;
                double w = uProfile.FlangeWidth * scaleFactor;
                double tw = uProfile.WebThickness * scaleFactor;
                double tf = uProfile.FlangeThickness * scaleFactor;

                double halfD = d / 2.0;
                double halfW = w / 2.0;

                // U-shape: two flanges and a web at the bottom, opening upward
                var points = new List<Point3d>
                {
                    new Point3d(-halfW,      -halfD,       0),
                    new Point3d( halfW,      -halfD,       0),
                    new Point3d( halfW,       halfD,       0),
                    new Point3d( halfW - tw,  halfD,       0),
                    new Point3d( halfW - tw, -halfD + tf,  0),
                    new Point3d(-halfW + tw, -halfD + tf,  0),
                    new Point3d(-halfW + tw,  halfD,       0),
                    new Point3d(-halfW,       halfD,       0),
                };

                return points;
            }

            // Handle IfcLShapeProfileDef
            var lProfile = profile as IIfcLShapeProfileDef;
            if (lProfile != null)
            {
                double d = lProfile.Depth * scaleFactor;
                double w = (lProfile.Width ?? lProfile.Depth) * scaleFactor;
                double t = lProfile.Thickness * scaleFactor;

                double halfD = d / 2.0;
                double halfW = w / 2.0;

                var points = new List<Point3d>
                {
                    new Point3d(-halfW,     -halfD,     0),
                    new Point3d( halfW,     -halfD,     0),
                    new Point3d( halfW,     -halfD + t, 0),
                    new Point3d(-halfW + t, -halfD + t, 0),
                    new Point3d(-halfW + t,  halfD,     0),
                    new Point3d(-halfW,      halfD,     0),
                };

                return points;
            }

            // Handle IfcIShapeProfileDef
            var iProfile = profile as IIfcIShapeProfileDef;
            if (iProfile != null)
            {
                double d = iProfile.OverallDepth * scaleFactor;
                double w = iProfile.OverallWidth * scaleFactor;
                double tw = iProfile.WebThickness * scaleFactor;
                double tf = iProfile.FlangeThickness * scaleFactor;

                double halfD = d / 2.0;
                double halfW = w / 2.0;
                double halfTw = tw / 2.0;

                var points = new List<Point3d>
                {
                    new Point3d(-halfW,   -halfD,       0),
                    new Point3d( halfW,   -halfD,       0),
                    new Point3d( halfW,   -halfD + tf,  0),
                    new Point3d( halfTw,  -halfD + tf,  0),
                    new Point3d( halfTw,   halfD - tf,  0),
                    new Point3d( halfW,    halfD - tf,  0),
                    new Point3d( halfW,    halfD,       0),
                    new Point3d(-halfW,    halfD,       0),
                    new Point3d(-halfW,    halfD - tf,  0),
                    new Point3d(-halfTw,   halfD - tf,  0),
                    new Point3d(-halfTw,  -halfD + tf,  0),
                    new Point3d(-halfW,   -halfD + tf,  0),
                };

                return points;
            }

            RhinoApp.WriteLine($"SERhinoIFC: Unsupported profile type: {profile.GetType().Name}");
            return null;
        }

        private Transform GetPlacementTransform(IIfcAxis2Placement3D placement, double scaleFactor)
        {
            if (placement == null) return Transform.Identity;

            var loc = placement.Location.Coordinates.ToArray();
            double ox = loc.Length > 0 ? loc[0] * scaleFactor : 0;
            double oy = loc.Length > 1 ? loc[1] * scaleFactor : 0;
            double oz = loc.Length > 2 ? loc[2] * scaleFactor : 0;

            // Default axes
            var zAxis = new Vector3d(0, 0, 1);
            var xAxis = new Vector3d(1, 0, 0);

            if (placement.Axis != null)
            {
                var ratios = placement.Axis.DirectionRatios.ToArray();
                if (ratios.Length >= 3)
                    zAxis = new Vector3d(ratios[0], ratios[1], ratios[2]);
            }

            if (placement.RefDirection != null)
            {
                var ratios = placement.RefDirection.DirectionRatios.ToArray();
                if (ratios.Length >= 3)
                    xAxis = new Vector3d(ratios[0], ratios[1], ratios[2]);
            }

            var yAxis = Vector3d.CrossProduct(zAxis, xAxis);
            xAxis.Unitize();
            yAxis.Unitize();
            zAxis.Unitize();

            return new Transform
            {
                M00 = xAxis.X, M01 = yAxis.X, M02 = zAxis.X, M03 = ox,
                M10 = xAxis.Y, M11 = yAxis.Y, M12 = zAxis.Y, M13 = oy,
                M20 = xAxis.Z, M21 = yAxis.Z, M22 = zAxis.Z, M23 = oz,
                M30 = 0, M31 = 0, M32 = 0, M33 = 1
            };
        }

        private IIfcProduct FindOwningProduct(IModel model, IIfcRepresentationItem repItem)
        {
            // Strategy 1: Use entity label matching to find the ShapeRepresentation containing this item
            int targetLabel = repItem.EntityLabel;
            foreach (var rep in model.Instances.OfType<IIfcShapeRepresentation>())
            {
                bool found = false;
                foreach (var item in rep.Items)
                {
                    if (item.EntityLabel == targetLabel)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found) continue;

                // Found the representation, now find the product via entity label matching
                int repLabel = rep.EntityLabel;
                foreach (var prodRep in model.Instances.OfType<IIfcProductDefinitionShape>())
                {
                    bool hasRep = false;
                    foreach (var r in prodRep.Representations)
                    {
                        if (r.EntityLabel == repLabel)
                        {
                            hasRep = true;
                            break;
                        }
                    }
                    if (!hasRep) continue;

                    int prodRepLabel = prodRep.EntityLabel;
                    var product = model.Instances.OfType<IIfcProduct>()
                        .FirstOrDefault(p => p.Representation != null && p.Representation.EntityLabel == prodRepLabel);
                    if (product != null) return product;
                }
            }

            // Strategy 2: Walk through IfcBooleanResult CSG trees
            foreach (var boolResult in model.Instances.OfType<IIfcBooleanResult>())
            {
                if (boolResult.FirstOperand is IIfcRepresentationItem first && first.EntityLabel == targetLabel)
                    return FindOwningProduct(model, boolResult);
                if (boolResult.SecondOperand is IIfcRepresentationItem second && second.EntityLabel == targetLabel)
                    return FindOwningProduct(model, boolResult);
            }

            return null;
        }

        private int AddMeshToDoc(RhinoDoc doc, Mesh rhinoMesh, IIfcProduct element,
            Dictionary<int, string> storeyLookup)
        {
            string elementName = element.Name?.ToString() ?? element.GetType().Name;
            string className = element.ExpressType.ExpressName;
            string storeyName = storeyLookup.ContainsKey(element.EntityLabel)
                ? storeyLookup[element.EntityLabel]
                : "Unsorted";

            string layerPath = $"{storeyName}::{className}";
            int layerIndex = GetOrCreateLayer(doc, layerPath);

            var attributes = new ObjectAttributes
            {
                Name = elementName,
                LayerIndex = layerIndex
            };

            SetAllMetadata(attributes, element);

            doc.Objects.AddMesh(rhinoMesh, attributes);
            return 1;
        }

        private void SetAllMetadata(ObjectAttributes attributes, IIfcProduct element)
        {
            // 1. IFC entity type
            attributes.SetUserString("IfcType", element.ExpressType.ExpressName);

            // 2. Standard IFC attributes
            attributes.SetUserString("GlobalId", element.GlobalId.ToString());

            if (element.Name.HasValue)
                attributes.SetUserString("Name", element.Name.Value.ToString());
            if (element.Description.HasValue)
                attributes.SetUserString("Description", element.Description.Value.ToString());
            if (element.ObjectType.HasValue)
                attributes.SetUserString("ObjectType", element.ObjectType.Value.ToString());

            if (element is IIfcObject obj && obj.ObjectType.HasValue)
                attributes.SetUserString("ObjectType", obj.ObjectType.Value.ToString());

            // Tag (on IfcElement)
            if (element is IIfcElement elem && elem.Tag.HasValue)
                attributes.SetUserString("Tag", elem.Tag.Value.ToString());

            // PredefinedType — varies by subtype, use reflection-free approach via known interfaces
            SetPredefinedType(attributes, element);

            // 3. All IfcPropertySet properties via IfcRelDefinesByProperties
            var propRels = element.IsDefinedBy
                .OfType<IIfcRelDefinesByProperties>();
            foreach (var rel in propRels)
            {
                if (rel.RelatingPropertyDefinition is IIfcPropertySet pset)
                {
                    string psetName = pset.Name?.ToString() ?? "PropertySet";
                    foreach (var prop in pset.HasProperties)
                    {
                        string key = $"{psetName}.{prop.Name}";
                        string value = GetPropertyValue(prop);
                        if (value != null)
                            attributes.SetUserString(key, value);
                    }
                }
                else if (rel.RelatingPropertyDefinition is IIfcPropertySetDefinitionSelect psetSelect)
                {
                    // Handle property set definition sets (IFC4)
                    if (psetSelect is IIfcPropertySetDefinition singleDef)
                    {
                        WritePropertySetDefinition(attributes, singleDef);
                    }
                }
            }

            // 4. All IfcQuantitySet values via IfcRelDefinesByProperties → IfcElementQuantity
            foreach (var rel in propRels)
            {
                if (rel.RelatingPropertyDefinition is IIfcElementQuantity qset)
                {
                    string qsetName = qset.Name?.ToString() ?? "Quantities";
                    foreach (var q in qset.Quantities)
                    {
                        string key = $"{qsetName}.{q.Name}";
                        string value = GetQuantityValue(q);
                        if (value != null)
                            attributes.SetUserString(key, value);
                    }
                }
            }

            // 5. Type properties (from IfcRelDefinesByType → IfcTypeObject)
            var typeRels = element.IsTypedBy;
            if (typeRels != null)
            {
                foreach (var typeRel in typeRels)
                {
                    var typeObj = typeRel.RelatingType;
                    if (typeObj != null)
                    {
                        attributes.SetUserString("TypeName", typeObj.Name?.ToString() ?? "");

                        if (typeObj.HasPropertySets != null)
                        {
                            foreach (var psetDef in typeObj.HasPropertySets)
                            {
                                WritePropertySetDefinition(attributes, psetDef);
                            }
                        }
                    }
                }
            }

            // 6. Material associations
            var materialRels = element.HasAssociations
                .OfType<IIfcRelAssociatesMaterial>();
            foreach (var matRel in materialRels)
            {
                var matSelect = matRel.RelatingMaterial;
                SetMaterialInfo(attributes, matSelect);
            }
        }

        private void WritePropertySetDefinition(ObjectAttributes attributes, IIfcPropertySetDefinition psetDef)
        {
            if (psetDef is IIfcPropertySet pset)
            {
                string psetName = pset.Name?.ToString() ?? "PropertySet";
                foreach (var prop in pset.HasProperties)
                {
                    string key = $"{psetName}.{prop.Name}";
                    string value = GetPropertyValue(prop);
                    if (value != null)
                        attributes.SetUserString(key, value);
                }
            }
            else if (psetDef is IIfcElementQuantity qset)
            {
                string qsetName = qset.Name?.ToString() ?? "Quantities";
                foreach (var q in qset.Quantities)
                {
                    string key = $"{qsetName}.{q.Name}";
                    string value = GetQuantityValue(q);
                    if (value != null)
                        attributes.SetUserString(key, value);
                }
            }
        }

        private string GetPropertyValue(IIfcProperty prop)
        {
            if (prop is IIfcPropertySingleValue single)
            {
                if (single.NominalValue == null) return null;
                string val = single.NominalValue.Value?.ToString() ?? "";
                // Append unit if present
                if (single.Unit is IIfcSIUnit siUnit)
                {
                    string prefix = siUnit.Prefix.HasValue ? siUnit.Prefix.Value.ToString() + " " : "";
                    val += $" [{prefix}{siUnit.Name}]";
                }
                else if (single.Unit is IIfcConversionBasedUnit convUnit)
                {
                    val += $" [{convUnit.Name}]";
                }
                return val;
            }
            if (prop is IIfcPropertyEnumeratedValue enumVal)
            {
                var values = enumVal.EnumerationValues?.Select(v => v.Value?.ToString() ?? "");
                return values != null ? string.Join(", ", values) : null;
            }
            if (prop is IIfcPropertyBoundedValue bounded)
            {
                string upper = bounded.UpperBoundValue?.Value?.ToString() ?? "?";
                string lower = bounded.LowerBoundValue?.Value?.ToString() ?? "?";
                return $"{lower} - {upper}";
            }
            if (prop is IIfcPropertyListValue listVal)
            {
                var values = listVal.ListValues?.Select(v => v.Value?.ToString() ?? "");
                return values != null ? string.Join(", ", values) : null;
            }
            if (prop is IIfcPropertyTableValue tableVal)
            {
                return $"[Table: {tableVal.DefiningValues?.Count() ?? 0} entries]";
            }
            if (prop is IIfcComplexProperty complex)
            {
                // Recurse into sub-properties won't fit user strings well,
                // so flatten with dot notation
                return $"[Complex: {complex.HasProperties.Count()} sub-properties]";
            }
            return prop.ToString();
        }

        private string GetQuantityValue(IIfcPhysicalQuantity q)
        {
            if (q is IIfcQuantityLength ql)
                return $"{ql.LengthValue} [length]";
            if (q is IIfcQuantityArea qa)
                return $"{qa.AreaValue} [area]";
            if (q is IIfcQuantityVolume qv)
                return $"{qv.VolumeValue} [volume]";
            if (q is IIfcQuantityWeight qw)
                return $"{qw.WeightValue} [weight]";
            if (q is IIfcQuantityCount qc)
                return $"{qc.CountValue} [count]";
            if (q is IIfcQuantityTime qt)
                return $"{qt.TimeValue} [time]";
            return q.ToString();
        }

        private void SetPredefinedType(ObjectAttributes attributes, IIfcProduct element)
        {
            // Check common element types for PredefinedType
            string predefined = null;
            if (element is IIfcWall wall) predefined = wall.PredefinedType?.ToString();
            else if (element is IIfcColumn col) predefined = col.PredefinedType?.ToString();
            else if (element is IIfcBeam beam) predefined = beam.PredefinedType?.ToString();
            else if (element is IIfcSlab slab) predefined = slab.PredefinedType?.ToString();
            else if (element is IIfcDoor door) predefined = door.PredefinedType?.ToString();
            else if (element is IIfcWindow win) predefined = win.PredefinedType?.ToString();
            else if (element is IIfcRoof roof) predefined = roof.PredefinedType?.ToString();
            else if (element is IIfcStair stair) predefined = stair.PredefinedType?.ToString();
            else if (element is IIfcRailing rail) predefined = rail.PredefinedType?.ToString();
            else if (element is IIfcMember member) predefined = member.PredefinedType?.ToString();
            else if (element is IIfcPlate plate) predefined = plate.PredefinedType?.ToString();
            else if (element is IIfcCovering cov) predefined = cov.PredefinedType?.ToString();
            else if (element is IIfcCurtainWall cw) predefined = cw.PredefinedType?.ToString();
            else if (element is IIfcBuildingElementProxy proxy) predefined = proxy.PredefinedType?.ToString();
            else if (element is IIfcPile pile) predefined = pile.PredefinedType?.ToString();
            else if (element is IIfcFooting footing) predefined = footing.PredefinedType?.ToString();
            else if (element is IIfcSpace space) predefined = space.PredefinedType?.ToString();

            if (!string.IsNullOrEmpty(predefined))
                attributes.SetUserString("PredefinedType", predefined);
        }

        private void SetMaterialInfo(ObjectAttributes attributes, IIfcMaterialSelect matSelect)
        {
            if (matSelect is IIfcMaterial mat)
            {
                attributes.SetUserString("Material.Name", mat.Name.ToString());
            }
            else if (matSelect is IIfcMaterialList matList)
            {
                var names = matList.Materials.Select(m => m.Name.ToString());
                attributes.SetUserString("Material.Name", string.Join("; ", names));
            }
            else if (matSelect is IIfcMaterialLayerSetUsage layerUsage)
            {
                SetMaterialLayerSet(attributes, layerUsage.ForLayerSet);
            }
            else if (matSelect is IIfcMaterialLayerSet layerSet)
            {
                SetMaterialLayerSet(attributes, layerSet);
            }
            else if (matSelect is IIfcMaterialProfileSetUsage profileUsage)
            {
                var profSet = profileUsage.ForProfileSet;
                if (profSet != null)
                {
                    if (profSet.Name.HasValue)
                        attributes.SetUserString("Material.ProfileSet", profSet.Name.Value.ToString());

                    int idx = 0;
                    foreach (var mp in profSet.MaterialProfiles)
                    {
                        if (mp.Material != null)
                            attributes.SetUserString($"Material.Profile[{idx}].Name", mp.Material.Name.ToString());
                        if (mp.Profile != null && mp.Profile.ProfileName.HasValue)
                            attributes.SetUserString($"Material.Profile[{idx}].Profile", mp.Profile.ProfileName.Value.ToString());
                        idx++;
                    }
                }
            }
            else if (matSelect is IIfcMaterialConstituentSet constSet)
            {
                if (constSet.Name.HasValue)
                    attributes.SetUserString("Material.ConstituentSet", constSet.Name.Value.ToString());

                foreach (var constituent in constSet.MaterialConstituents)
                {
                    string cName = constituent.Name?.ToString() ?? "Unnamed";
                    if (constituent.Material != null)
                        attributes.SetUserString($"Material.{cName}", constituent.Material.Name.ToString());
                }
            }
            else if (matSelect is IIfcMaterialLayer layer)
            {
                if (layer.Material != null)
                    attributes.SetUserString("Material.Name", layer.Material.Name.ToString());
                attributes.SetUserString("Material.LayerThickness", layer.LayerThickness.ToString());
            }
            else if (matSelect is IIfcMaterialProfile profile)
            {
                if (profile.Material != null)
                    attributes.SetUserString("Material.Name", profile.Material.Name.ToString());
                if (profile.Profile != null && profile.Profile.ProfileName.HasValue)
                    attributes.SetUserString("Material.Profile", profile.Profile.ProfileName.Value.ToString());
            }
        }

        private void SetMaterialLayerSet(ObjectAttributes attributes, IIfcMaterialLayerSet layerSet)
        {
            if (layerSet == null) return;
            if (layerSet.LayerSetName.HasValue)
                attributes.SetUserString("Material.LayerSet", layerSet.LayerSetName.Value.ToString());

            int idx = 0;
            foreach (var layer in layerSet.MaterialLayers)
            {
                if (layer.Material != null)
                    attributes.SetUserString($"Material.Layer[{idx}].Name", layer.Material.Name.ToString());
                attributes.SetUserString($"Material.Layer[{idx}].Thickness", layer.LayerThickness.ToString());
                idx++;
            }
        }

        private int AddOrphanMeshToDoc(RhinoDoc doc, Mesh rhinoMesh, string name,
            Dictionary<int, string> storeyLookup)
        {
            string layerPath = "Unsorted::OrphanGeometry";
            int layerIndex = GetOrCreateLayer(doc, layerPath);

            var attributes = new ObjectAttributes
            {
                Name = name,
                LayerIndex = layerIndex
            };

            doc.Objects.AddMesh(rhinoMesh, attributes);
            return 1;
        }

        private Mesh ConvertToRhinoMesh(IXbimShapeGeometryData shapeGeometry, XbimMatrix3D transform, double scaleFactor)
        {
            var rhinoMesh = new Mesh();

            var ms = new System.IO.MemoryStream(shapeGeometry.ShapeData);
            var br = new System.IO.BinaryReader(ms);

            var meshData = br.ReadShapeTriangulation();
            if (meshData == null)
                return null;

            int vertexOffset = rhinoMesh.Vertices.Count;
            foreach (var vertex in meshData.Vertices)
            {
                var pt = transform.Transform(vertex);
                rhinoMesh.Vertices.Add(
                    (float)(pt.X * scaleFactor),
                    (float)(pt.Y * scaleFactor),
                    (float)(pt.Z * scaleFactor));
            }

            foreach (var face in meshData.Faces)
            {
                var indices = face.Indices;
                for (int i = 0; i < indices.Count; i += 3)
                {
                    rhinoMesh.Faces.AddFace(
                        vertexOffset + indices[i],
                        vertexOffset + indices[i + 1],
                        vertexOffset + indices[i + 2]);
                }
            }

            rhinoMesh.Normals.ComputeNormals();
            rhinoMesh.Compact();
            return rhinoMesh;
        }

        private Dictionary<int, string> BuildStoreyLookup(IModel model)
        {
            var lookup = new Dictionary<int, string>();

            var spatialRels = model.Instances.OfType<IIfcRelContainedInSpatialStructure>();
            foreach (var rel in spatialRels)
            {
                var storey = rel.RelatingStructure as IIfcBuildingStorey;
                if (storey == null) continue;

                string storeyName = storey.Name?.ToString() ?? $"Storey {storey.EntityLabel}";
                foreach (var element in rel.RelatedElements)
                {
                    lookup[element.EntityLabel] = storeyName;
                }
            }

            return lookup;
        }

        private int GetOrCreateLayer(RhinoDoc doc, string layerPath)
        {
            int existingIndex = doc.Layers.FindByFullPath(layerPath, -1);
            if (existingIndex >= 0)
                return existingIndex;

            var parts = layerPath.Split(new[] { "::" }, StringSplitOptions.None);
            int parentIndex = -1;

            for (int i = 0; i < parts.Length; i++)
            {
                string partName = parts[i];
                string fullPath = string.Join("::", parts, 0, i + 1);
                int idx = doc.Layers.FindByFullPath(fullPath, -1);

                if (idx >= 0)
                {
                    parentIndex = idx;
                }
                else
                {
                    var layer = new Layer { Name = partName };
                    if (parentIndex >= 0)
                        layer.ParentLayerId = doc.Layers[parentIndex].Id;

                    parentIndex = doc.Layers.Add(layer);
                }
            }

            return parentIndex;
        }
    }
}
