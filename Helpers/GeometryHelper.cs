using System;
using Rhino.Geometry;

namespace SERhinoIFC.Helpers
{
    public static class GeometryHelper
    {
        /// <summary>
        /// Gets or creates a triangulated mesh from a Rhino geometry object.
        /// </summary>
        public static Mesh GetTriangulatedMesh(GeometryBase geometry)
        {
            Mesh mesh = null;

            if (geometry is Mesh m)
            {
                mesh = m.DuplicateMesh();
            }
            else if (geometry is Brep brep)
            {
                var meshes = Mesh.CreateFromBrep(brep, MeshingParameters.Default);
                if (meshes != null && meshes.Length > 0)
                {
                    mesh = new Mesh();
                    foreach (var part in meshes)
                        mesh.Append(part);
                }
            }
            else if (geometry is Extrusion extrusion)
            {
                var brep2 = extrusion.ToBrep();
                if (brep2 != null)
                {
                    var meshes = Mesh.CreateFromBrep(brep2, MeshingParameters.Default);
                    if (meshes != null && meshes.Length > 0)
                    {
                        mesh = new Mesh();
                        foreach (var part in meshes)
                            mesh.Append(part);
                    }
                }
            }

            if (mesh != null)
            {
                mesh.Faces.ConvertQuadsToTriangles();
                mesh.Compact();
            }

            return mesh;
        }
    }
}
