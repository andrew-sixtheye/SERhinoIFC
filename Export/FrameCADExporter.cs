using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using SERhinoIFC.Helpers;
using Xbim.Common.Step21;
using Xbim.Ifc;
using Xbim.Ifc2x3.GeometricConstraintResource;
using Xbim.Ifc2x3.GeometricModelResource;
using Xbim.Ifc2x3.GeometryResource;
using Xbim.Ifc2x3.Kernel;
using Xbim.Ifc2x3.MeasureResource;
using Xbim.Ifc2x3.ProductExtension;
using Xbim.Ifc2x3.ProfileResource;
using Xbim.Ifc2x3.RepresentationResource;
using Xbim.Ifc2x3.SharedBldgElements;
using Xbim.Ifc2x3.TopologyResource;
using Xbim.IO;

namespace SERhinoIFC.Export
{
    public class FrameCADExporter
    {
        public int Export(RhinoObject[] objects, string filePath, ExportOptions options, RhinoDoc doc)
        {
            int exportedCount = 0;
            double scaleFactor = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Millimeters);
            var metadataWriter = new IfcMetadataWriter();

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
                using (var txn = model.BeginTransaction("FrameCAD Export"))
                {
                    // Project and units (millimeters)
                    var project = model.Instances.New<IfcProject>(p =>
                    {
                        p.Name = System.IO.Path.GetFileNameWithoutExtension(doc.Name ?? "FrameCADExport");
                    });

                    var unitAssignment = model.Instances.New<IfcUnitAssignment>();
                    unitAssignment.Units.Add(model.Instances.New<IfcSIUnit>(u =>
                    {
                        u.UnitType = IfcUnitEnum.LENGTHUNIT;
                        u.Name = IfcSIUnitName.METRE;
                        u.Prefix = IfcSIPrefix.MILLI;
                    }));
                    unitAssignment.Units.Add(model.Instances.New<IfcSIUnit>(u =>
                    {
                        u.UnitType = IfcUnitEnum.AREAUNIT;
                        u.Name = IfcSIUnitName.SQUARE_METRE;
                    }));
                    unitAssignment.Units.Add(model.Instances.New<IfcSIUnit>(u =>
                    {
                        u.UnitType = IfcUnitEnum.VOLUMEUNIT;
                        u.Name = IfcSIUnitName.CUBIC_METRE;
                    }));
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

                    // Spatial hierarchy
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

                    // Group objects by storey (top-level layer)
                    var storeyGroups = new Dictionary<string, List<RhinoObject>>();
                    foreach (var obj in objects)
                    {
                        string storeyName = IfcMetadataWriter.GetStoreyName(obj, doc);
                        if (!storeyGroups.ContainsKey(storeyName))
                            storeyGroups[storeyName] = new List<RhinoObject>();
                        storeyGroups[storeyName].Add(obj);
                    }

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
                            // Extract member geometry (axis + profile) from Rhino geometry
                            var memberGeom = ExtractMemberGeometry(rhinoObj, scaleFactor);
                            if (memberGeom == null)
                                continue;

                            // Create IFC element from user text metadata
                            var element = metadataWriter.CreateElement(model, rhinoObj, doc);
                            if (element == null) continue;

                            // Apply all metadata (properties, quantities, materials)
                            metadataWriter.ApplyMetadata(model, element, rhinoObj);

                            // Build profile definition from extracted geometry
                            var profileDef = CreateProfileDef(model, memberGeom);

                            // Build ExtrudedAreaSolid
                            var extrudedSolid = model.Instances.New<IfcExtrudedAreaSolid>(eas =>
                            {
                                eas.SweptArea = profileDef;
                                eas.Position = model.Instances.New<IfcAxis2Placement3D>(a =>
                                {
                                    a.Location = model.Instances.New<IfcCartesianPoint>(p => p.SetXYZ(0, 0, 0));
                                });
                                eas.ExtrudedDirection = model.Instances.New<IfcDirection>(d =>
                                    d.SetXYZ(0, 0, 1));
                                eas.Depth = memberGeom.Length;
                            });

                            var shapeRep = model.Instances.New<IfcShapeRepresentation>(sr =>
                            {
                                sr.ContextOfItems = repContext;
                                sr.RepresentationIdentifier = "Body";
                                sr.RepresentationType = "SweptSolid";
                                sr.Items.Add(extrudedSolid);
                            });

                            var productShape = model.Instances.New<IfcProductDefinitionShape>(ps =>
                            {
                                ps.Representations.Add(shapeRep);
                            });

                            element.Representation = productShape;

                            // Object placement: Z axis = member axis, X axis = ref direction
                            element.ObjectPlacement = CreateMemberPlacement(model, memberGeom);

                            containment.RelatedElements.Add(element);
                            exportedCount++;
                        }
                    }

                    txn.Commit();
                }

                model.SaveAs(filePath, StorageType.Ifc);
            }

            try { System.IO.File.Delete(xbimPath); } catch { }

            return exportedCount;
        }

        #region Geometry Extraction

        private MemberGeometry ExtractMemberGeometry(RhinoObject rhinoObj, double scaleFactor)
        {
            var geom = rhinoObj.Geometry;

            if (geom is Extrusion extrusion)
                return ExtractFromExtrusion(extrusion, scaleFactor);

            if (geom is Brep brep)
                return ExtractFromBrep(brep, scaleFactor, rhinoObj.Name);

            if (geom is Curve)
            {
                RhinoApp.WriteLine($"Object '{rhinoObj.Name}' is a curve -- skipped. Model as Extrusion or Brep.");
                return null;
            }

            RhinoApp.WriteLine($"Object '{rhinoObj.Name}' is not a supported geometry type -- skipped.");
            return null;
        }

        private MemberGeometry ExtractFromExtrusion(Extrusion extrusion, double scaleFactor)
        {
            var pathStart = extrusion.PathStart;
            var pathEnd = extrusion.PathEnd;
            var axisDir = extrusion.PathTangent;
            axisDir.Unitize();
            double length = pathStart.DistanceTo(pathEnd) * scaleFactor;

            var profileCurve = extrusion.Profile3d(0, 0);
            var profilePoints = GetProfilePoints2D(profileCurve, axisDir, pathStart, scaleFactor);

            return new MemberGeometry
            {
                StartPoint = new Point3d(pathStart.X * scaleFactor, pathStart.Y * scaleFactor, pathStart.Z * scaleFactor),
                AxisDirection = axisDir,
                RefDirection = ComputeRefDirection(axisDir),
                Length = length,
                ProfilePoints = profilePoints,
                ProfileWidth = EstimateProfileWidth(profilePoints),
                ProfileDepth = EstimateProfileDepth(profilePoints)
            };
        }

        private MemberGeometry ExtractFromBrep(Brep brep, double scaleFactor, string name)
        {
            var planarFaces = new List<(BrepFace face, Plane plane)>();
            foreach (var face in brep.Faces)
            {
                if (face.IsPlanar() && face.TryGetPlane(out Plane plane))
                    planarFaces.Add((face, plane));
            }

            for (int i = 0; i < planarFaces.Count; i++)
            {
                for (int j = i + 1; j < planarFaces.Count; j++)
                {
                    var n1 = planarFaces[i].plane.Normal;
                    var n2 = planarFaces[j].plane.Normal;
                    double dot = Math.Abs(n1 * n2);
                    if (dot > 0.999)
                    {
                        var centroid1 = AreaMassProperties.Compute(planarFaces[i].face)?.Centroid ?? Point3d.Origin;
                        var centroid2 = AreaMassProperties.Compute(planarFaces[j].face)?.Centroid ?? Point3d.Origin;

                        var axisDir = centroid2 - centroid1;
                        double length = axisDir.Length * scaleFactor;
                        axisDir.Unitize();

                        var boundary = planarFaces[i].face.OuterLoop?.To3dCurve();
                        var profilePoints = boundary != null
                            ? GetProfilePoints2D(boundary, axisDir, centroid1, scaleFactor)
                            : null;

                        return new MemberGeometry
                        {
                            StartPoint = new Point3d(centroid1.X * scaleFactor, centroid1.Y * scaleFactor, centroid1.Z * scaleFactor),
                            AxisDirection = axisDir,
                            RefDirection = ComputeRefDirection(axisDir),
                            Length = length,
                            ProfilePoints = profilePoints,
                            ProfileWidth = profilePoints != null ? EstimateProfileWidth(profilePoints) : 45.0,
                            ProfileDepth = profilePoints != null ? EstimateProfileDepth(profilePoints) : 150.0
                        };
                    }
                }
            }

            RhinoApp.WriteLine($"Object '{name}' does not have two parallel planar cap faces -- skipped.");
            return null;
        }

        private List<Point2d> GetProfilePoints2D(Curve profileCurve, Vector3d axisDir, Point3d origin, double scaleFactor)
        {
            if (profileCurve == null) return null;

            var refDir = ComputeRefDirection(axisDir);
            var yDir = Vector3d.CrossProduct(axisDir, refDir);
            yDir.Unitize();

            var plane = new Plane(origin, refDir, yDir);

            var polyline = profileCurve.ToPolyline(0.01, 0.1, 0, 0);
            if (polyline == null) return null;

            var pts = new List<Point2d>();
            var pl = polyline.ToPolyline();
            if (pl == null) return null;

            foreach (var pt in pl)
            {
                double u, v;
                plane.ClosestParameter(pt, out u, out v);
                pts.Add(new Point2d(u * scaleFactor, v * scaleFactor));
            }

            if (pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) < 0.01)
                pts.RemoveAt(pts.Count - 1);

            return pts.Count >= 3 ? pts : null;
        }

        private Vector3d ComputeRefDirection(Vector3d axisDir)
        {
            var worldX = Vector3d.XAxis;
            if (Math.Abs(axisDir * worldX) > 0.95)
                worldX = Vector3d.YAxis;

            var refDir = Vector3d.CrossProduct(axisDir, worldX);
            refDir.Unitize();
            var corrected = Vector3d.CrossProduct(refDir, axisDir);
            corrected.Unitize();
            return corrected;
        }

        private double EstimateProfileWidth(List<Point2d> points)
        {
            if (points == null || points.Count < 2) return 45.0;
            return Math.Abs(points.Max(p => p.X) - points.Min(p => p.X));
        }

        private double EstimateProfileDepth(List<Point2d> points)
        {
            if (points == null || points.Count < 2) return 150.0;
            return Math.Abs(points.Max(p => p.Y) - points.Min(p => p.Y));
        }

        #endregion

        #region IFC Entity Creation

        private IfcProfileDef CreateProfileDef(IfcStore model, MemberGeometry geom)
        {
            if (geom.ProfilePoints != null && geom.ProfilePoints.Count >= 3)
            {
                var polyline = model.Instances.New<IfcPolyline>(pl =>
                {
                    foreach (var pt in geom.ProfilePoints)
                    {
                        pl.Points.Add(model.Instances.New<IfcCartesianPoint>(cp =>
                            cp.SetXY(pt.X, pt.Y)));
                    }
                    var first = geom.ProfilePoints[0];
                    pl.Points.Add(model.Instances.New<IfcCartesianPoint>(cp =>
                        cp.SetXY(first.X, first.Y)));
                });

                return model.Instances.New<IfcArbitraryClosedProfileDef>(p =>
                {
                    p.ProfileType = IfcProfileTypeEnum.AREA;
                    p.OuterCurve = polyline;
                });
            }
            else
            {
                RhinoApp.WriteLine("  Warning: No profile data available, using default 150x45mm rectangle.");
                return model.Instances.New<IfcRectangleProfileDef>(p =>
                {
                    p.ProfileType = IfcProfileTypeEnum.AREA;
                    p.XDim = 45.0;
                    p.YDim = 150.0;
                    p.Position = model.Instances.New<IfcAxis2Placement2D>(a =>
                    {
                        a.Location = model.Instances.New<IfcCartesianPoint>(cp => cp.SetXY(0, 0));
                    });
                });
            }
        }

        private IfcLocalPlacement CreateMemberPlacement(IfcStore model, MemberGeometry geom)
        {
            return model.Instances.New<IfcLocalPlacement>(lp =>
            {
                lp.RelativePlacement = model.Instances.New<IfcAxis2Placement3D>(a =>
                {
                    a.Location = model.Instances.New<IfcCartesianPoint>(p =>
                        p.SetXYZ(geom.StartPoint.X, geom.StartPoint.Y, geom.StartPoint.Z));

                    a.Axis = model.Instances.New<IfcDirection>(d =>
                        d.SetXYZ(geom.AxisDirection.X, geom.AxisDirection.Y, geom.AxisDirection.Z));

                    a.RefDirection = model.Instances.New<IfcDirection>(d =>
                        d.SetXYZ(geom.RefDirection.X, geom.RefDirection.Y, geom.RefDirection.Z));
                });
            });
        }

        #endregion
    }

    internal class MemberGeometry
    {
        public Point3d StartPoint { get; set; }
        public Vector3d AxisDirection { get; set; }
        public Vector3d RefDirection { get; set; }
        public double Length { get; set; }
        public List<Point2d> ProfilePoints { get; set; }
        public double ProfileWidth { get; set; }
        public double ProfileDepth { get; set; }
    }
}
