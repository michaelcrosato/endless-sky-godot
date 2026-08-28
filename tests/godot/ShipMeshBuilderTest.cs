namespace EndlessSky.Tests.Presentation
{
    using EndlessSky.Data;
    using EndlessSky.Game;
    using EndlessSky.Sim;
    using GdUnit4;
    using Godot;
    using static GdUnit4.Assertions;

    /// <summary>
    /// Presentation-tier guards for <see cref="ShipMeshBuilder"/>.
    /// </summary>
    /// <remarks>
    /// This suite exists because of a specific miss. Generated hulls shipped with
    /// their face normals inverted — Godot treats CLOCKWISE winding as front-facing,
    /// so the outward normal is (c-a)x(b-a) and the counter-clockwise convention
    /// gives its negation. The geometry still drew, because culling keys off winding
    /// rather than off the normal attribute, so nothing crashed and nothing failed:
    /// every outward face simply carried an inward normal, N.L went negative across
    /// the whole lit side, and hulls rendered as flat black silhouettes. With rim
    /// lighting on they rendered as flat white ones instead, since Godot's rim term
    /// is not scaled by N.L.
    ///
    /// The full 373-test simulation suite was blind to all of it: a mesh is not a
    /// simulation value. These assertions run in a real engine and check the two
    /// things a rendered hull needs and a headless test can still prove — that the
    /// normals exist, and that they point away from the hull rather than into it.
    /// </remarks>
    [TestSuite]
    public class ShipMeshBuilderTest
    {
        private static ShipAppearance Appearance(string name, double mass, string[] hardpoints)
        {
            var text = new System.Text.StringBuilder();
            text.Append($"ship \"{name}\"\n\tattributes\n\t\t\"mass\" {mass}\n\t\t\"hull\" 500\n");
            foreach (string hardpoint in hardpoints)
                text.Append($"\t{hardpoint}\n");

            var definition = new ShipDefinition(name);
            definition.Load(new DataFile(text.ToString(), "test.txt").Nodes[0]);
            return new ShipAppearance(definition);
        }

        private static ArrayMesh HullMesh(ShipAppearance appearance, int damageState = 0)
        {
            Node3D root = ShipMeshBuilder.Build(appearance, damageState);
            var body = root.GetNode<MeshInstance3D>("Body");
            var mesh = (ArrayMesh)body.Mesh;
            root.Free();
            return mesh;
        }

        /// <summary>
        /// A hull whose faces span every orientation, so the check is not satisfied
        /// by a lucky subset.
        /// </summary>
        private static ShipAppearance Subject() => Appearance("Test Hull", 630, new[]
        {
            "engine -9 32", "engine 9 32",
            "gun -6 -30", "gun 6 -30",
            "turret 0 -4",
        });

        [TestCase]
        [RequireGodotRuntime]
        public void EverySurfaceCarriesNormals()
        {
            ArrayMesh mesh = HullMesh(Subject());

            AssertThat(mesh.GetSurfaceCount()).IsGreater(0);

            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            {
                Godot.Collections.Array arrays = mesh.SurfaceGetArrays(surface);
                var normals = (Vector3[])arrays[(int)Mesh.ArrayType.Normal];
                var vertices = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];

                AssertThat(normals).IsNotNull();
                AssertThat(normals.Length).IsEqual(vertices.Length);
            }
        }

        [TestCase]
        [RequireGodotRuntime]
        public void FaceNormalsPointAwayFromTheHullCentre()
        {
            // The regression guard. On a closed shell every face's outward normal has
            // a positive dot with the vector from the hull's centre to that face; an
            // inverted normal makes it negative for essentially every face at once.
            ArrayMesh mesh = HullMesh(Subject());

            var all = new System.Collections.Generic.List<Vector3>();
            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                all.AddRange((Vector3[])mesh.SurfaceGetArrays(surface)[(int)Mesh.ArrayType.Vertex]);

            Vector3 centre = Vector3.Zero;
            foreach (Vector3 vertex in all)
                centre += vertex;
            centre /= all.Count;

            int outward = 0, inward = 0;

            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            {
                Godot.Collections.Array arrays = mesh.SurfaceGetArrays(surface);
                var vertices = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];
                var normals = (Vector3[])arrays[(int)Mesh.ArrayType.Normal];

                for (int i = 0; i + 2 < vertices.Length; i += 3)
                {
                    Vector3 faceCentre = (vertices[i] + vertices[i + 1] + vertices[i + 2]) / 3f;
                    Vector3 outFromCentre = faceCentre - centre;
                    if (outFromCentre.LengthSquared() < 1e-8f)
                        continue;

                    if (normals[i].Dot(outFromCentre) >= 0f)
                        outward++;
                    else
                        inward++;
                }
            }

            AssertThat(outward + inward).IsGreater(0);

            // Not every face on a lofted, non-convex hull points strictly outward, so
            // this is a strong majority rather than an absolute. The inverted-normal
            // bug flipped essentially all of them, which this catches with room spare.
            AssertBool(outward > inward * 4)
                .OverrideFailureMessage(
                    $"{inward} of {outward + inward} faces point into the hull; " +
                    "face normals are inverted (Godot front faces wind clockwise)")
                .IsTrue();
        }

        [TestCase]
        [RequireGodotRuntime]
        public void TheDorsalAndVentralSurfacesAreBothPresentAndDistinct()
        {
            // The value break between the lit top and the shadowed underside is what
            // holds a hull's silhouette at gameplay distance; a single surface reads
            // as a featureless blob whatever its albedo.
            ArrayMesh mesh = HullMesh(Subject());

            AssertThat(mesh.GetSurfaceCount()).IsEqual(2);

            var dorsal = (StandardMaterial3D)mesh.SurfaceGetMaterial(0);
            var ventral = (StandardMaterial3D)mesh.SurfaceGetMaterial(1);

            AssertThat(dorsal).IsNotNull();
            AssertThat(ventral).IsNotNull();
            AssertBool(ventral.AlbedoColor.V < dorsal.AlbedoColor.V)
                .OverrideFailureMessage(
                    $"ventral ({ventral.AlbedoColor.V:F3}) must sit below " +
                    $"dorsal ({dorsal.AlbedoColor.V:F3}) in value")
                .IsTrue();
        }

        [TestCase]
        [RequireGodotRuntime]
        public void MountsAreBuiltInsideTheHullTheyBelongTo()
        {
            // Mount offsets are sprite-scale data and the hull is sized from them, so
            // a fitting must land on its hull. Sizing hulls from mass instead once put
            // 85% of the armed fleet's mounts outside their own geometry.
            ShipAppearance appearance = Subject();
            Node3D root = ShipMeshBuilder.Build(appearance, 0);

            float halfLength = WorldSpace.Length(appearance.Length) * 0.5f;
            float halfBeam = WorldSpace.Length(appearance.Beam) * 0.5f;
            int fittings = 0;

            foreach (Node child in root.GetChildren())
            {
                if (child is not MeshInstance3D fitting || fitting.Name == "Body")
                    continue;

                fittings++;
                AssertBool(Mathf.Abs(fitting.Position.Z) <= halfLength + 0.001f &&
                           Mathf.Abs(fitting.Position.X) <= halfBeam + 0.001f)
                    .OverrideFailureMessage(
                        $"{fitting.Name} at {fitting.Position} is outside the hull " +
                        $"(half-length {halfLength:F2}, half-beam {halfBeam:F2})")
                    .IsTrue();
            }

            AssertThat(fittings).IsGreater(0);
            root.Free();
        }
    }
}
