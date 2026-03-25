using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
using Xbim.Ifc2x3.MaterialPropertyResource;
using Xbim.Ifc2x3.MaterialResource;
using Xbim.Ifc2x3.MeasureResource;
using Xbim.Ifc2x3.PresentationOrganizationResource;
using Xbim.Ifc2x3.ProductExtension;
using Xbim.Ifc2x3.ProfileResource;
using Xbim.Ifc2x3.PropertyResource;
using Xbim.Ifc2x3.RepresentationResource;
using Xbim.Ifc2x3.SharedBldgElements;
using Xbim.Ifc2x3.TopologyResource;
using Xbim.IO;

namespace SERhinoIFC.Export
{
    public class FrameCADExporter
    {
        // Regex for valid member tokens: T#, B#, H#, W#, R#, SILL#
        private static readonly Regex MemberTokenPattern =
            new Regex(@"^(T|B|H|W|R|SILL)\d+$", RegexOptions.IgnoreCase);

        // Regex for <prefix>-<token> format
        private static readonly Regex FullNamePattern =
            new Regex(@"^.+-(T|B|H|W|R|SILL)\d+$", RegexOptions.IgnoreCase);

        public int Export(RhinoObject[] objects, string filePath, ExportOptions options, RhinoDoc doc)
        {
            int exportedCount = 0;
            string prefix = options.FrameNamePrefix ?? "F1";
            double scaleFactor = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Millimeters);

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
                    // Set up units — millimeters
                    var project = model.Instances.New<IfcProject>(p =>
                    {
                        p.Name = System.IO.Path.GetFileNameWithoutExtension(doc.Name ?? "FrameCADExport");
                    });

                    var unitAssignment = model.Instances.New<IfcUnitAssignment>();
                    var lengthUnit = model.Instances.New<IfcSIUnit>(u =>
                    {
                        u.UnitType = IfcUnitEnum.LENGTHUNIT;
                        u.Name = IfcSIUnitName.METRE;
                        u.Prefix = IfcSIPrefix.MILLI;
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

                    var storey = model.Instances.New<IfcBuildingStorey>(s =>
                    {
                        s.Name = "Level 1";
                        s.CompositionType = IfcElementCompositionEnum.ELEMENT;
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
                    model.Instances.New<IfcRelAggregates>(r =>
                    {
                        r.RelatingObject = building;
                        r.RelatedObjects.Add(storey);
                    });

                    var containment = model.Instances.New<IfcRelContainedInSpatialStructure>(r =>
                    {
                        r.RelatingStructure = storey;
                    });

                    // Create shared material with steel properties
                    var material = model.Instances.New<IfcMaterial>(m =>
                    {
                        m.Name = "Cold-formed Steel";
                    });

                    // Track member names and tokens for layer classification
                    var memberData = new List<(IfcProduct element, string memberName, string token)>();
                    int autoIndex = 1;

                    foreach (var rhinoObj in objects)
                    {
                        // Extract axis + profile from geometry
                        var memberGeom = ExtractMemberGeometry(rhinoObj, scaleFactor);
                        if (memberGeom == null)
                            continue;

                        // Resolve member name
                        string memberName = ResolveMemberName(rhinoObj, prefix, ref autoIndex);
                        string token = ExtractToken(memberName);

                        RhinoApp.WriteLine($"  {rhinoObj.Name ?? "(unnamed)"} -> {memberName}");

                        // Determine IFC type from token
                        bool isColumn = token.StartsWith("W", StringComparison.OrdinalIgnoreCase);

                        // Create the IFC element
                        IfcProduct element;
                        if (isColumn)
                        {
                            element = model.Instances.New<IfcColumn>(e => e.Name = memberName);
                        }
                        else
                        {
                            element = model.Instances.New<IfcMember>(e => e.Name = memberName);
                        }

                        // Build the profile definition
                        var profileDef = CreateProfileDef(model, memberGeom);

                        // Build IFCEXTRUDEDAREASOLID
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

                        // Shape representation
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
                        memberData.Add((element, memberName, token));

                        // Material association for this element
                        model.Instances.New<IfcRelAssociatesMaterial>(ram =>
                        {
                            ram.RelatingMaterial = material;
                            ram.RelatedObjects.Add(element);
                        });

                        // Profile property set
                        CreateProfileProperties(model, element, profileDef, memberGeom);

                        exportedCount++;
                    }

                    // Material properties: YieldStress and UltimateStress
                    RhinoApp.WriteLine("Note: Using default cold-formed steel values (550 MPa yield/ultimate).");
                    CreateMaterialProperties(model, material);

                    // Layer assignment: TRUSS vs PANEL
                    AssignLayers(model, memberData, prefix);

                    txn.Commit();
                }

                model.SaveAs(filePath, StorageType.Ifc);
            }

            try { System.IO.File.Delete(xbimPath); } catch { }

            return exportedCount;
        }

        #region Member Naming

        private string ResolveMemberName(RhinoObject obj, string prefix, ref int autoIndex)
        {
            string name = obj.Name;

            // Case 1: Already has <prefix>-<token> format
            if (!string.IsNullOrEmpty(name) && FullNamePattern.IsMatch(name))
                return name;

            // Case 2: Name has no dash — prepend prefix
            if (!string.IsNullOrEmpty(name) && !name.Contains("-"))
            {
                if (MemberTokenPattern.IsMatch(name))
                    return $"{prefix}-{name}";
                // Name doesn't match a token, use it as prefix with auto token
                return $"{prefix}-{name}";
            }

            // Case 3: Empty name — auto assign W<index>
            if (string.IsNullOrEmpty(name))
            {
                string result = $"{prefix}-W{autoIndex}";
                autoIndex++;
                return result;
            }

            // Fallback: use as-is if it has a dash
            return name;
        }

        private string ExtractToken(string memberName)
        {
            int dashIndex = memberName.LastIndexOf('-');
            if (dashIndex >= 0 && dashIndex < memberName.Length - 1)
                return memberName.Substring(dashIndex + 1);
            return memberName;
        }

        #endregion

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
                RhinoApp.WriteLine($"Object '{rhinoObj.Name}' is a curve with no profile data -- skipped. Model as an Extrusion object to export.");
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

            // Get profile curve in 2D
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
            // Find two parallel planar faces (cap faces)
            var planarFaces = new List<(BrepFace face, Plane plane)>();
            foreach (var face in brep.Faces)
            {
                if (face.IsPlanar() && face.TryGetPlane(out Plane plane))
                    planarFaces.Add((face, plane));
            }

            // Find a pair of parallel planar faces
            for (int i = 0; i < planarFaces.Count; i++)
            {
                for (int j = i + 1; j < planarFaces.Count; j++)
                {
                    var n1 = planarFaces[i].plane.Normal;
                    var n2 = planarFaces[j].plane.Normal;
                    // Check if parallel (normals are same or opposite direction)
                    double dot = Math.Abs(n1 * n2);
                    if (dot > 0.999)
                    {
                        var centroid1 = AreaMassProperties.Compute(planarFaces[i].face)?.Centroid ?? Point3d.Origin;
                        var centroid2 = AreaMassProperties.Compute(planarFaces[j].face)?.Centroid ?? Point3d.Origin;

                        var axisDir = centroid2 - centroid1;
                        double length = axisDir.Length * scaleFactor;
                        axisDir.Unitize();

                        // Use the first cap face boundary as the profile
                        var boundary = planarFaces[i].face.OuterLoop?.To3dCurve();
                        var profilePoints = boundary != null
                            ? GetProfilePoints2D(boundary, axisDir, centroid1, scaleFactor)
                            : null;

                        var startPt = centroid1;

                        return new MemberGeometry
                        {
                            StartPoint = new Point3d(startPt.X * scaleFactor, startPt.Y * scaleFactor, startPt.Z * scaleFactor),
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
            if (profileCurve == null)
                return null;

            // Build a local coordinate system: Z = axis, X = refDir, Y = cross
            var refDir = ComputeRefDirection(axisDir);
            var yDir = Vector3d.CrossProduct(axisDir, refDir);
            yDir.Unitize();

            var plane = new Plane(origin, refDir, yDir);

            // Discretize the curve
            var polyline = profileCurve.ToPolyline(0.01, 0.1, 0, 0);
            if (polyline == null)
                return null;

            var pts = new List<Point2d>();
            var pl = polyline.ToPolyline();
            if (pl == null) return null;

            foreach (var pt in pl)
            {
                double u, v;
                plane.ClosestParameter(pt, out u, out v);
                pts.Add(new Point2d(u * scaleFactor, v * scaleFactor));
            }

            // Remove duplicate closing point if present
            if (pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) < 0.01)
                pts.RemoveAt(pts.Count - 1);

            return pts.Count >= 3 ? pts : null;
        }

        private Vector3d ComputeRefDirection(Vector3d axisDir)
        {
            // Choose a reference direction perpendicular to the axis
            // Use world X unless axis is nearly parallel to X
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
            double minX = points.Min(p => p.X);
            double maxX = points.Max(p => p.X);
            return Math.Abs(maxX - minX);
        }

        private double EstimateProfileDepth(List<Point2d> points)
        {
            if (points == null || points.Count < 2) return 150.0;
            double minY = points.Min(p => p.Y);
            double maxY = points.Max(p => p.Y);
            return Math.Abs(maxY - minY);
        }

        #endregion

        #region IFC Entity Creation

        private IfcProfileDef CreateProfileDef(IfcStore model, MemberGeometry geom)
        {
            if (geom.ProfilePoints != null && geom.ProfilePoints.Count >= 3)
            {
                // Use arbitrary closed profile from actual profile points
                var polyline = model.Instances.New<IfcPolyline>(pl =>
                {
                    foreach (var pt in geom.ProfilePoints)
                    {
                        pl.Points.Add(model.Instances.New<IfcCartesianPoint>(cp =>
                            cp.SetXY(pt.X, pt.Y)));
                    }
                    // Close the polyline
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
                // Fallback: 150x45mm rectangle
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

                    // Z axis = member axis direction
                    a.Axis = model.Instances.New<IfcDirection>(d =>
                        d.SetXYZ(geom.AxisDirection.X, geom.AxisDirection.Y, geom.AxisDirection.Z));

                    // X axis = reference direction (web direction)
                    a.RefDirection = model.Instances.New<IfcDirection>(d =>
                        d.SetXYZ(geom.RefDirection.X, geom.RefDirection.Y, geom.RefDirection.Z));
                });
            });
        }

        private void CreateProfileProperties(IfcStore model, IfcProduct element,
            IfcProfileDef profileDef, MemberGeometry geom)
        {
            // Create a property set with profile dimensions
            var pset = model.Instances.New<IfcPropertySet>(ps =>
            {
                ps.Name = "FrameCAD_ProfileProperties";
                ps.HasProperties.Add(model.Instances.New<IfcPropertySingleValue>(pv =>
                {
                    pv.Name = "WebDepth";
                    pv.NominalValue = new Xbim.Ifc2x3.MeasureResource.IfcLengthMeasure(geom.ProfileDepth);
                }));
                ps.HasProperties.Add(model.Instances.New<IfcPropertySingleValue>(pv =>
                {
                    pv.Name = "FlangeWidth";
                    pv.NominalValue = new Xbim.Ifc2x3.MeasureResource.IfcLengthMeasure(geom.ProfileWidth);
                }));
            });

            model.Instances.New<IfcRelDefinesByProperties>(rd =>
            {
                rd.RelatingPropertyDefinition = pset;
                rd.RelatedObjects.Add(element);
            });
        }

        private void CreateMaterialProperties(IfcStore model, IfcMaterial material)
        {
            // Use IfcMechanicalSteelMaterialProperties which has native YieldStress/UltimateStress
            model.Instances.New<IfcMechanicalSteelMaterialProperties>(sp =>
            {
                sp.Material = material;
                sp.YieldStress = 550.0;
                sp.UltimateStress = 550.0;
            });
        }

        private void AssignLayers(IfcStore model,
            List<(IfcProduct element, string memberName, string token)> memberData,
            string frameName)
        {
            if (memberData.Count == 0) return;

            // Determine if this is a truss or panel frame
            // Truss: has both T*/B* (chords) and W* (webs) tokens
            bool hasChords = memberData.Any(m =>
                m.token.StartsWith("T", StringComparison.OrdinalIgnoreCase) ||
                m.token.StartsWith("B", StringComparison.OrdinalIgnoreCase));
            bool hasWebs = memberData.Any(m =>
                m.token.StartsWith("W", StringComparison.OrdinalIgnoreCase));
            bool isTruss = hasChords && hasWebs;

            string layerName = isTruss ? $"{frameName}_TRUSS" : $"{frameName}_PANEL";

            var layer = model.Instances.New<IfcPresentationLayerAssignment>(pla =>
            {
                pla.Name = layerName;
                foreach (var md in memberData)
                {
                    // Add the shape representations to the layer
                    var rep = md.element.Representation;
                    if (rep != null)
                    {
                        foreach (var shapeRep in rep.Representations)
                        {
                            pla.AssignedItems.Add(shapeRep);
                        }
                    }
                }
            });
        }

        #endregion
    }

    /// <summary>
    /// Intermediate data structure for extracted member geometry.
    /// </summary>
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
