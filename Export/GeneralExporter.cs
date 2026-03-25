using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using SERhinoIFC.Helpers;
using Xbim.Common.Step21;
using Xbim.Ifc;
using Xbim.Ifc2x3.GeometricModelResource;
using Xbim.Ifc2x3.GeometryResource;
using Xbim.Ifc2x3.Kernel;
using Xbim.Ifc2x3.MeasureResource;
using Xbim.Ifc2x3.ProductExtension;
using Xbim.Ifc2x3.RepresentationResource;
using Xbim.Ifc2x3.SharedBldgElements;
using Xbim.Ifc2x3.GeometricConstraintResource;
using Xbim.Ifc2x3.TopologyResource;
using Xbim.IO;

namespace SERhinoIFC.Export
{
    public class GeneralExporter
    {
        public int Export(RhinoObject[] objects, string filePath, ExportOptions options, RhinoDoc doc)
        {
            int exportedCount = 0;
            double scaleFactor = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);

            var credentials = new XbimEditorCredentials
            {
                ApplicationDevelopersName = "SERhinoIFC",
                ApplicationFullName = "SERhinoIFC Plugin",
                ApplicationIdentifier = "SERhinoIFC",
                ApplicationVersion = "1.0",
                EditorsFamilyName = options.Author ?? Environment.UserName,
                EditorsOrganisationName = options.Organization ?? ""
            };

            string xbimPath = System.IO.Path.ChangeExtension(filePath, ".xbim");
            using (var model = IfcStore.Create(xbimPath, credentials, XbimSchemaVersion.Ifc2X3))
            {
                using (var txn = model.BeginTransaction("Create IFC"))
                {
                    // Set up units — meters, no prefix
                    var project = model.Instances.New<IfcProject>(p =>
                    {
                        p.Name = System.IO.Path.GetFileNameWithoutExtension(doc.Name ?? "RhinoExport");
                    });

                    var unitAssignment = model.Instances.New<IfcUnitAssignment>();
                    var lengthUnit = model.Instances.New<IfcSIUnit>(u =>
                    {
                        u.UnitType = IfcUnitEnum.LENGTHUNIT;
                        u.Name = IfcSIUnitName.METRE;
                    });
                    unitAssignment.Units.Add(lengthUnit);

                    var areaUnit = model.Instances.New<IfcSIUnit>(u =>
                    {
                        u.UnitType = IfcUnitEnum.AREAUNIT;
                        u.Name = IfcSIUnitName.SQUARE_METRE;
                    });
                    unitAssignment.Units.Add(areaUnit);

                    var volumeUnit = model.Instances.New<IfcSIUnit>(u =>
                    {
                        u.UnitType = IfcUnitEnum.VOLUMEUNIT;
                        u.Name = IfcSIUnitName.CUBIC_METRE;
                    });
                    unitAssignment.Units.Add(volumeUnit);

                    project.UnitsInContext = unitAssignment;

                    // Representation context
                    var repContext = model.Instances.New<IfcGeometricRepresentationContext>(c =>
                    {
                        c.ContextType = "Model";
                        c.CoordinateSpaceDimension = 3;
                        c.Precision = 1e-5;
                        c.WorldCoordinateSystem = model.Instances.New<IfcAxis2Placement3D>(a =>
                        {
                            a.Location = model.Instances.New<IfcCartesianPoint>(p2 =>
                                p2.SetXYZ(0, 0, 0));
                        });
                    });
                    project.RepresentationContexts.Add(repContext);

                    // Spatial hierarchy: Project -> Site -> Building -> Storey(s)
                    var site = model.Instances.New<IfcSite>(s =>
                    {
                        s.Name = "Default Site";
                        s.CompositionType = IfcElementCompositionEnum.ELEMENT;
                    });

                    var building = model.Instances.New<IfcBuilding>(b =>
                    {
                        b.Name = "Default Building";
                        b.CompositionType = IfcElementCompositionEnum.ELEMENT;
                    });

                    // Aggregation relationships
                    model.Instances.New<IfcRelAggregates>(r =>
                    {
                        r.RelatingObject = project;
                        r.RelatedObjects.Add(site);
                    });

                    model.Instances.New<IfcRelAggregates>(r =>
                    {
                        r.RelatingObject = site;
                        r.RelatedObjects.Add(building);
                    });

                    // Group objects by top-level layer (used as storey)
                    var storeyGroups = new Dictionary<string, List<RhinoObject>>();
                    foreach (var obj in objects)
                    {
                        var layer = doc.Layers[obj.Attributes.LayerIndex];
                        string topLayerName = GetTopLevelLayerName(layer, doc);
                        if (!storeyGroups.ContainsKey(topLayerName))
                            storeyGroups[topLayerName] = new List<RhinoObject>();
                        storeyGroups[topLayerName].Add(obj);
                    }

                    // Create storeys and elements
                    foreach (var kvp in storeyGroups)
                    {
                        var storey = model.Instances.New<IfcBuildingStorey>(s =>
                        {
                            s.Name = kvp.Key;
                            s.CompositionType = IfcElementCompositionEnum.ELEMENT;
                        });

                        model.Instances.New<IfcRelAggregates>(r =>
                        {
                            r.RelatingObject = building;
                            r.RelatedObjects.Add(storey);
                        });

                        var containment = model.Instances.New<IfcRelContainedInSpatialStructure>(r =>
                        {
                            r.RelatingStructure = storey;
                        });

                        foreach (var rhinoObj in kvp.Value)
                        {
                            var mesh = GeometryHelper.GetTriangulatedMesh(rhinoObj.Geometry);
                            if (mesh == null || mesh.Vertices.Count < 3)
                            {
                                RhinoApp.WriteLine($"Skipping '{rhinoObj.Name}': could not generate mesh.");
                                continue;
                            }

                            // Determine IFC type from layer name
                            var layer = doc.Layers[rhinoObj.Attributes.LayerIndex];
                            string ifcTypeName = MemberClassifier.GetIfcTypeFromLayerName(layer.FullPath);

                            // Create the IFC element
                            var element = CreateIfcElement(model, ifcTypeName, rhinoObj.Name);
                            if (element == null) continue;

                            // Create geometry
                            var brep = CreateFacetedBrep(model, mesh, scaleFactor);
                            if (brep == null) continue;

                            var shapeRep = model.Instances.New<IfcShapeRepresentation>(sr =>
                            {
                                sr.ContextOfItems = repContext;
                                sr.RepresentationIdentifier = "Body";
                                sr.RepresentationType = "Brep";
                                sr.Items.Add(brep);
                            });

                            var productShape = model.Instances.New<IfcProductDefinitionShape>(ps =>
                            {
                                ps.Representations.Add(shapeRep);
                            });

                            element.Representation = productShape;

                            // Object placement at origin
                            element.ObjectPlacement = model.Instances.New<IfcLocalPlacement>(lp =>
                            {
                                lp.RelativePlacement = model.Instances.New<IfcAxis2Placement3D>(a =>
                                {
                                    a.Location = model.Instances.New<IfcCartesianPoint>(p2 =>
                                        p2.SetXYZ(0, 0, 0));
                                });
                            });

                            containment.RelatedElements.Add(element);
                            exportedCount++;
                        }
                    }

                    txn.Commit();
                }

                model.SaveAs(filePath, StorageType.Ifc);
            }

            // Clean up temp xbim database file
            try { System.IO.File.Delete(xbimPath); } catch { }

            return exportedCount;
        }

        private IfcProduct CreateIfcElement(IfcStore model, string ifcTypeName, string name)
        {
            string elementName = string.IsNullOrEmpty(name) ? ifcTypeName : name;

            switch (ifcTypeName)
            {
                case "IfcWall":
                    return model.Instances.New<IfcWall>(e => e.Name = elementName);
                case "IfcSlab":
                    return model.Instances.New<IfcSlab>(e => e.Name = elementName);
                case "IfcColumn":
                    return model.Instances.New<IfcColumn>(e => e.Name = elementName);
                case "IfcBeam":
                    return model.Instances.New<IfcBeam>(e => e.Name = elementName);
                case "IfcRoof":
                    return model.Instances.New<IfcRoof>(e => e.Name = elementName);
                default:
                    return model.Instances.New<IfcBuildingElementProxy>(e => e.Name = elementName);
            }
        }

        private IfcFacetedBrep CreateFacetedBrep(IfcStore model, Mesh mesh, double scaleFactor)
        {
            // Create all cartesian points from mesh vertices
            var ifcPoints = new IfcCartesianPoint[mesh.Vertices.Count];
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                var v = mesh.Vertices[i];
                ifcPoints[i] = model.Instances.New<IfcCartesianPoint>(p =>
                    p.SetXYZ(v.X * scaleFactor, v.Y * scaleFactor, v.Z * scaleFactor));
            }

            // Create faces from triangulated mesh
            var shell = model.Instances.New<IfcClosedShell>();

            for (int i = 0; i < mesh.Faces.Count; i++)
            {
                var meshFace = mesh.Faces[i];
                int a = meshFace.A, b = meshFace.B, c = meshFace.C;

                var polyLoop = model.Instances.New<IfcPolyLoop>(pl =>
                {
                    pl.Polygon.Add(ifcPoints[a]);
                    pl.Polygon.Add(ifcPoints[b]);
                    pl.Polygon.Add(ifcPoints[c]);
                });

                var faceBound = model.Instances.New<IfcFaceOuterBound>(fb =>
                {
                    fb.Bound = polyLoop;
                    fb.Orientation = true;
                });

                var face = model.Instances.New<IfcFace>(f =>
                {
                    f.Bounds.Add(faceBound);
                });

                shell.CfsFaces.Add(face);
            }

            var brep = model.Instances.New<IfcFacetedBrep>(fb =>
            {
                fb.Outer = shell;
            });

            return brep;
        }

        private string GetTopLevelLayerName(Layer layer, RhinoDoc doc)
        {
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
