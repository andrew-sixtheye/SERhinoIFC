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
            var targetUnits = ExportUnitHelper.ToRhinoUnitSystem(options.Units);
            double scaleFactor = RhinoMath.UnitScale(doc.ModelUnitSystem, targetUnits);
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

            using (var model = IfcStore.Create(credentials, XbimSchemaVersion.Ifc2X3, XbimStoreType.InMemoryModel))
            {
                using (var txn = model.BeginTransaction("FrameCAD Export"))
                {
                    // Project and units (millimeters)
                    var project = model.Instances.New<IfcProject>(p =>
                    {
                        p.Name = System.IO.Path.GetFileNameWithoutExtension(doc.Name ?? "FrameCADExport");
                    });

                    var unitAssignment = model.Instances.New<IfcUnitAssignment>();
                    ExportUnitHelper.CreateLengthUnit(model, unitAssignment, options.Units);
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
                        c.Precision = options.Tolerance;
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

            if (geom is Mesh mesh)
                return ExtractFromMesh(mesh, scaleFactor, rhinoObj.Name);

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
            // Group all planar faces by their normal direction.
            // Cap faces share the same normal (or opposite), side faces have different normals.
            // The two groups of coplanar faces that are furthest apart are the caps.
            var planarFaces = new List<(BrepFace face, Plane plane)>();
            foreach (var face in brep.Faces)
            {
                if (face.IsPlanar() && face.TryGetPlane(out Plane plane))
                    planarFaces.Add((face, plane));
            }

            if (planarFaces.Count < 2)
            {
                RhinoApp.WriteLine($"Object '{name}' has fewer than 2 planar faces -- skipped.");
                return null;
            }

            // Group faces by normal direction (parallel normals within tolerance)
            var normalGroups = new List<List<(BrepFace face, Plane plane)>>();
            foreach (var pf in planarFaces)
            {
                bool added = false;
                foreach (var group in normalGroups)
                {
                    double dot = Math.Abs(pf.plane.Normal * group[0].plane.Normal);
                    if (dot > 0.999)
                    {
                        group.Add(pf);
                        added = true;
                        break;
                    }
                }
                if (!added)
                    normalGroups.Add(new List<(BrepFace, Plane)> { pf });
            }

            // Find the pair of face groups with the same normal direction that are
            // separated by the greatest distance (these are the two cap ends)
            BrepFace capFace1 = null, capFace2 = null;
            Point3d cap1Center = Point3d.Origin, cap2Center = Point3d.Origin;
            double maxDist = 0;

            foreach (var group in normalGroups)
            {
                // Split group into coplanar sub-groups (faces at different positions along the normal)
                var subGroups = new List<List<(BrepFace face, Point3d centroid)>>();
                foreach (var (face, plane) in group)
                {
                    var centroid = AreaMassProperties.Compute(face)?.Centroid ?? Point3d.Origin;
                    bool matched = false;
                    foreach (var sg in subGroups)
                    {
                        double distAlongNormal = Math.Abs((centroid - sg[0].centroid) * plane.Normal);
                        if (distAlongNormal < 0.01) // coplanar
                        {
                            sg.Add((face, centroid));
                            matched = true;
                            break;
                        }
                    }
                    if (!matched)
                        subGroups.Add(new List<(BrepFace, Point3d)> { (face, centroid) });
                }

                // Check pairs of sub-groups for maximum separation
                for (int i = 0; i < subGroups.Count; i++)
                {
                    for (int j = i + 1; j < subGroups.Count; j++)
                    {
                        var c1 = subGroups[i][0].centroid;
                        var c2 = subGroups[j][0].centroid;
                        double dist = c1.DistanceTo(c2);
                        if (dist > maxDist)
                        {
                            maxDist = dist;
                            // Pick the largest face from each sub-group as the cap
                            capFace1 = subGroups[i].OrderByDescending(f =>
                                AreaMassProperties.Compute(f.face)?.Area ?? 0).First().face;
                            capFace2 = subGroups[j].OrderByDescending(f =>
                                AreaMassProperties.Compute(f.face)?.Area ?? 0).First().face;
                            cap1Center = c1;
                            cap2Center = c2;
                        }
                    }
                }
            }

            if (capFace1 == null || capFace2 == null)
            {
                RhinoApp.WriteLine($"Object '{name}' could not identify cap faces -- skipped.");
                return null;
            }

            var axisDir = cap2Center - cap1Center;
            double length = axisDir.Length * scaleFactor;
            axisDir.Unitize();

            // Extract the profile from the cap face boundary
            var boundary = capFace1.OuterLoop?.To3dCurve();
            var profilePoints = boundary != null
                ? GetProfilePoints2D(boundary, axisDir, cap1Center, scaleFactor)
                : null;

            return new MemberGeometry
            {
                StartPoint = new Point3d(cap1Center.X * scaleFactor, cap1Center.Y * scaleFactor, cap1Center.Z * scaleFactor),
                AxisDirection = axisDir,
                RefDirection = ComputeRefDirection(axisDir),
                Length = length,
                ProfilePoints = profilePoints,
                ProfileWidth = profilePoints != null ? EstimateProfileWidth(profilePoints) : 45.0,
                ProfileDepth = profilePoints != null ? EstimateProfileDepth(profilePoints) : 150.0
            };
        }

        private MemberGeometry ExtractFromMesh(Mesh mesh, double scaleFactor, string name)
        {
            if (mesh == null || mesh.Vertices.Count < 6 || mesh.Faces.Count < 4)
            {
                RhinoApp.WriteLine($"Object '{name}' mesh is too simple -- skipped.");
                return null;
            }

            mesh.FaceNormals.ComputeFaceNormals();

            // Group mesh faces by normal direction (parallel normals within tolerance)
            var normalGroups = new List<(Vector3f normal, List<int> faceIndices)>();
            for (int i = 0; i < mesh.Faces.Count; i++)
            {
                var fn = mesh.FaceNormals[i];
                bool added = false;
                foreach (var group in normalGroups)
                {
                    double dot = Math.Abs(group.normal.X * fn.X + group.normal.Y * fn.Y + group.normal.Z * fn.Z);
                    if (dot > 0.99)
                    {
                        group.faceIndices.Add(i);
                        added = true;
                        break;
                    }
                }
                if (!added)
                    normalGroups.Add((fn, new List<int> { i }));
            }

            // Find end cap groups: two groups with opposite normals that are coplanar within each group
            // End caps have the most faces in their normal direction (for tessellated C-shapes, the caps
            // have many triangles). But actually, we want the pair with the greatest separation distance.
            double maxSeparation = 0;
            List<int> capFaces1 = null, capFaces2 = null;
            Vector3d axisDir = Vector3d.ZAxis;

            foreach (var group in normalGroups)
            {
                if (group.faceIndices.Count < 2) continue;

                // Split into coplanar sub-groups by projecting face centroids onto the normal
                var normal = new Vector3d(group.normal.X, group.normal.Y, group.normal.Z);
                var subGroups = new List<(double projVal, List<int> faces)>();

                foreach (int fi in group.faceIndices)
                {
                    var f = mesh.Faces[fi];
                    var centroid = (Point3d)(mesh.Vertices[f.A] + mesh.Vertices[f.B] + mesh.Vertices[f.C]) / 3.0;
                    double proj = centroid.X * normal.X + centroid.Y * normal.Y + centroid.Z * normal.Z;

                    bool matched = false;
                    foreach (var sg in subGroups)
                    {
                        if (Math.Abs(proj - sg.projVal) < 0.01)
                        {
                            sg.faces.Add(fi);
                            matched = true;
                            break;
                        }
                    }
                    if (!matched)
                        subGroups.Add((proj, new List<int> { fi }));
                }

                // Find the pair of sub-groups with maximum separation
                for (int i = 0; i < subGroups.Count; i++)
                {
                    for (int j = i + 1; j < subGroups.Count; j++)
                    {
                        double sep = Math.Abs(subGroups[i].projVal - subGroups[j].projVal);
                        if (sep > maxSeparation)
                        {
                            maxSeparation = sep;
                            capFaces1 = subGroups[i].faces;
                            capFaces2 = subGroups[j].faces;
                            axisDir = normal;
                        }
                    }
                }
            }

            if (capFaces1 == null || capFaces2 == null || maxSeparation < 0.1)
            {
                RhinoApp.WriteLine($"Object '{name}' could not identify end cap faces in mesh -- skipped.");
                return null;
            }

            axisDir.Unitize();

            // Collect unique vertices from cap1 faces
            var cap1VertexIndices = new HashSet<int>();
            foreach (int fi in capFaces1)
            {
                var f = mesh.Faces[fi];
                cap1VertexIndices.Add(f.A);
                cap1VertexIndices.Add(f.B);
                cap1VertexIndices.Add(f.C);
                if (f.IsQuad) cap1VertexIndices.Add(f.D);
            }

            // Compute centroids of each cap
            var cap1Pts = cap1VertexIndices.Select(vi => (Point3d)mesh.Vertices[vi]).ToList();
            var cap1Center = new Point3d(
                cap1Pts.Average(p => p.X),
                cap1Pts.Average(p => p.Y),
                cap1Pts.Average(p => p.Z));

            var cap2VertexIndices = new HashSet<int>();
            foreach (int fi in capFaces2)
            {
                var f = mesh.Faces[fi];
                cap2VertexIndices.Add(f.A);
                cap2VertexIndices.Add(f.B);
                cap2VertexIndices.Add(f.C);
                if (f.IsQuad) cap2VertexIndices.Add(f.D);
            }
            var cap2Pts = cap2VertexIndices.Select(vi => (Point3d)mesh.Vertices[vi]).ToList();
            var cap2Center = new Point3d(
                cap2Pts.Average(p => p.X),
                cap2Pts.Average(p => p.Y),
                cap2Pts.Average(p => p.Z));

            // Axis direction from cap1 to cap2
            var memberAxis = cap2Center - cap1Center;
            double length = memberAxis.Length * scaleFactor;
            memberAxis.Unitize();

            // Project cap1 vertices onto a plane perpendicular to the axis to get 2D profile
            var refDir = ComputeRefDirection(memberAxis);
            var yDir = Vector3d.CrossProduct(memberAxis, refDir);
            yDir.Unitize();
            var capPlane = new Plane(cap1Center, refDir, yDir);

            var profilePts = new List<Point2d>();
            foreach (var pt in cap1Pts)
            {
                double u, v;
                capPlane.ClosestParameter(pt, out u, out v);
                profilePts.Add(new Point2d(u * scaleFactor, v * scaleFactor));
            }

            // Order the profile points by angle around the centroid to form a proper polygon
            double cx = profilePts.Average(p => p.X);
            double cy = profilePts.Average(p => p.Y);
            profilePts = profilePts
                .OrderBy(p => Math.Atan2(p.Y - cy, p.X - cx))
                .ToList();

            // Remove near-duplicate points (tessellation creates many close vertices)
            var cleanedPts = new List<Point2d> { profilePts[0] };
            for (int i = 1; i < profilePts.Count; i++)
            {
                if (profilePts[i].DistanceTo(cleanedPts[cleanedPts.Count - 1]) > 0.1)
                    cleanedPts.Add(profilePts[i]);
            }
            profilePts = cleanedPts;

            if (profilePts.Count < 3)
            {
                RhinoApp.WriteLine($"Object '{name}' cap profile has fewer than 3 unique points -- skipped.");
                return null;
            }

            return new MemberGeometry
            {
                StartPoint = new Point3d(cap1Center.X * scaleFactor, cap1Center.Y * scaleFactor, cap1Center.Z * scaleFactor),
                AxisDirection = memberAxis,
                RefDirection = refDir,
                Length = length,
                ProfilePoints = profilePts,
                ProfileWidth = EstimateProfileWidth(profilePts),
                ProfileDepth = EstimateProfileDepth(profilePts)
            };
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
