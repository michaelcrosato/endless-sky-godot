using System;
using System.Collections.Generic;
using EndlessSky.Data;
using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// A standalone turntable for <see cref="ShipMeshBuilder"/>: it lays a row of
    /// hulls out close to camera under controlled light and writes a PNG.
    ///
    /// This exists because the flight scene is the wrong instrument for judging a
    /// mesh. There a ship is forty pixels wide, lit by whatever the system's star
    /// happens to be doing, so a geometry or material regression is invisible — the
    /// first M8 capture read as "white blobs" and the second as "black blobs" purely
    /// from material changes, with no way to tell shape from shading. Here each hull
    /// fills a useful fraction of the frame and the lighting is fixed, so a change
    /// to the builder is actually legible.
    ///
    /// Presentation-only and never loaded by the game: run it directly with
    /// <c>godot4-mono --path . res://scenes/meshpreview.tscn -- --capture=path.png</c>.
    /// </summary>
    public partial class ShipMeshPreview : Node3D
    {
        /// <summary>Hulls to line up, chosen to span the size classes.</summary>
        private static readonly string[] Subjects =
        {
            "Shuttle", "Marauder Raven", "Falcon", "Lance", "Bactrian",
        };

        private string? _capturePath;
        private int _damageState;
        private int _framesLeft = 8;

        public override void _Ready()
        {
            ParseArguments();
            BuildEnvironment();
            BuildLighting();
            BuildSubjects();
        }

        public override void _Process(double delta)
        {
            if (_capturePath is null)
                return;

            // Let the renderer settle before reading the framebuffer back.
            if (--_framesLeft > 0)
                return;

            Image image = GetViewport().GetTexture().GetImage();
            Error err = image.SavePng(_capturePath);
            GD.Print($"[meshpreview] capture {(err == Error.Ok ? "saved" : $"FAILED ({err})")}: {_capturePath}");
            _capturePath = null;
            GetTree().Quit();
        }

        private void ParseArguments()
        {
            foreach (string arg in OS.GetCmdlineUserArgs())
            {
                if (arg.StartsWith("--capture=", StringComparison.Ordinal))
                    _capturePath = arg["--capture=".Length..];
                else if (arg.StartsWith("--damage=", StringComparison.Ordinal) &&
                         int.TryParse(arg["--damage=".Length..], out int damage))
                    _damageState = Math.Clamp(damage, 0, 3);
            }
        }

        private void BuildEnvironment()
        {
            // Deliberately the same ambient and tonemap as the flight scene: a hull
            // that only reads under studio lighting has not actually been fixed.
            var environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.02f, 0.02f, 0.04f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.5f, 0.55f, 0.65f),
                AmbientLightEnergy = 0.08f,
                TonemapMode = Godot.Environment.ToneMapper.Aces,
            };
            AddChild(new WorldEnvironment { Environment = environment });
        }

        private void BuildLighting()
        {
            // Key raking down from front-left, matching the flight scene's colour and
            // energy but with the vertical component a top-down camera needs.
            var key = new DirectionalLight3D
            {
                Name = "Key",
                LightColor = new Color(1.0f, 0.94f, 0.85f),
                LightEnergy = 2.2f,
            };
            AddChild(key);
            key.LookAtFromPosition(new Vector3(-6f, 9f, 6f), Vector3.Zero, Vector3.Up);

            var fill = new DirectionalLight3D
            {
                Name = "Fill",
                LightColor = new Color(0.42f, 0.58f, 1.0f),
                LightEnergy = 0.30f,
            };
            AddChild(fill);
            fill.LookAtFromPosition(new Vector3(7f, -3f, -5f), Vector3.Zero, Vector3.Up);
        }

        private void BuildSubjects()
        {
            GameData? data = EsData.Universe;
            if (data is null)
            {
                GD.Print("[meshpreview] no dataset available; set ENDLESS_SKY_DATA");
                return;
            }

            var hulls = new List<(string Name, Node3D Node, float Width)>();
            float totalWidth = 0f;

            foreach (string name in Subjects)
            {
                ShipDefinition? definition = data.Ships.TryGetValue(name, out ShipDefinition? found) ? found : null;
                if (definition is null)
                {
                    GD.Print($"[meshpreview] no such ship: {name}");
                    continue;
                }

                var appearance = new ShipAppearance(definition)
                {
                    Faction = data.GovernmentOf(definition.DisplayName),
                };
                Node3D mesh = ShipMeshBuilder.Build(appearance, _damageState);

                // Normalise each hull to a common on-screen size. Real relative scale
                // is the flight scene's job; here the point is to see the geometry.
                float length = WorldSpace.Length(appearance.Length);
                float scale = 3.2f / Math.Max(0.001f, length);
                var holder = new Node3D { Name = name, Scale = new Vector3(scale, scale, scale) };
                holder.AddChild(mesh);

                var slot = new Node3D();
                slot.AddChild(holder);
                AddChild(slot);

                hulls.Add(($"{name} · {appearance.Faction ?? "(no faction)"}", slot, 4.2f));
                totalWidth += 4.2f;
            }

            float x = -totalWidth * 0.5f + 2.1f;
            foreach ((string name, Node3D node, float width) in hulls)
            {
                node.Position = new Vector3(x, 0f, 0f);
                // Yaw slightly so the silhouette is not a pure side-on profile.
                node.Rotation = new Vector3(0f, Mathf.DegToRad(28f), 0f);
                AddChild(NameTag(name, x));
                x += width;
            }

            var camera = new Camera3D { Name = "PreviewCamera", Fov = 45f };
            AddChild(camera);
            // Three-quarter high view: the angle a player actually sees ships from.
            camera.LookAtFromPosition(new Vector3(0f, 7.5f, 11.5f), new Vector3(0f, 0f, 0f), Vector3.Up);
        }

        private static Label3D NameTag(string text, float x) => new Label3D
        {
            Text = text,
            Position = new Vector3(x, -2.6f, 0f),
            FontSize = 48,
            PixelSize = 0.006f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Modulate = new Color(0.75f, 0.8f, 0.9f),
        };
    }
}
