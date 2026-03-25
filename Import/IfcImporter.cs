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
        public int Import(string filePath, RhinoDoc doc)
        {
            int objectCount = 0;

            using (var model = IfcStore.Open(filePath))
            {
                double scaleFactor = UnitResolver.GetScaleFactor(model, doc);

                // Tessellate all geometry
                var context = new Xbim3DModelContext(model);
                context.CreateContext();

                // Build lookup: element -> storey name
                var storeyLookup = BuildStoreyLookup(model);

                // Iterate shape instances
                var shapeInstances = context.ShapeInstances();

                foreach (var shapeInstance in shapeInstances)
                {
                    // Only process the highest-fidelity representation for each product
                    if (shapeInstance.RepresentationType != XbimGeometryRepresentationType.OpeningsAndAdditionsExcluded)
                        continue;

                    var shapeGeometry = context.ShapeGeometry(shapeInstance);
                    if (shapeGeometry == null)
                        continue;

                    var rhinoMesh = ConvertToRhinoMesh(shapeGeometry, shapeInstance.Transformation, scaleFactor);
                    if (rhinoMesh == null || rhinoMesh.Vertices.Count == 0)
                        continue;

                    // Get the IFC element info
                    var element = model.Instances[shapeInstance.IfcProductLabel] as IIfcProduct;
                    if (element == null)
                        continue;

                    string elementName = element.Name?.ToString() ?? element.GetType().Name;
                    string className = element.ExpressType.ExpressName;
                    string storeyName = storeyLookup.ContainsKey(element.EntityLabel)
                        ? storeyLookup[element.EntityLabel]
                        : "Unsorted";

                    // Create or find layer
                    string layerPath = $"{storeyName}::{className}";
                    int layerIndex = GetOrCreateLayer(doc, layerPath);

                    // Add mesh to document
                    var attributes = new ObjectAttributes
                    {
                        Name = elementName,
                        LayerIndex = layerIndex
                    };
                    attributes.SetUserString("IfcGlobalId", element.GlobalId.ToString());

                    doc.Objects.AddMesh(rhinoMesh, attributes);
                    objectCount++;
                }
            }

            return objectCount;
        }

        private Mesh ConvertToRhinoMesh(IXbimShapeGeometryData shapeGeometry, XbimMatrix3D transform, double scaleFactor)
        {
            var rhinoMesh = new Mesh();

            // Read the triangulated mesh data from xBIM
            var ms = new System.IO.MemoryStream(shapeGeometry.ShapeData);
            var br = new System.IO.BinaryReader(ms);

            var meshData = br.ReadShapeTriangulation();
            if (meshData == null)
                return null;

            // Vertices are shared across all faces in the triangulation
            int vertexOffset = rhinoMesh.Vertices.Count;
            foreach (var vertex in meshData.Vertices)
            {
                // Apply instance transformation
                var pt = transform.Transform(vertex);
                // Apply scale factor
                rhinoMesh.Vertices.Add(
                    (float)(pt.X * scaleFactor),
                    (float)(pt.Y * scaleFactor),
                    (float)(pt.Z * scaleFactor));
            }

            // Faces reference indices into the shared vertex list
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
            // layerPath format: "StoreyName::ClassName"
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
