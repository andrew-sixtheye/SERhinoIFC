using System;
using Rhino.Geometry;

namespace SERhinoIFC.Helpers
{
    public static class GeometryHelper
    {
        /// <summary>
        /// Gets or creates a triangulated mesh from a Rhino geometry object.
        /// Uses SimplePlanes to minimize vertex count on planar faces.
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
                mesh = MeshBrep(brep);
            }
            else if (geometry is Extrusion extrusion)
            {
                var brep2 = extrusion.ToBrep();
                if (brep2 != null)
                    mesh = MeshBrep(brep2);
            }

            if (mesh != null)
            {
                mesh.Faces.ConvertQuadsToTriangles();
                mesh.Compact();
            }

            return mesh;
        }

        private static Mesh MeshBrep(Brep brep)
        {
            var mp = new MeshingParameters();
            mp.SimplePlanes = true;
            var meshes = Mesh.CreateFromBrep(brep, mp);
            if (meshes == null || meshes.Length == 0)
                return null;

            var mesh = new Mesh();
            foreach (var part in meshes)
                mesh.Append(part);
            return mesh;
        }
    }
}
