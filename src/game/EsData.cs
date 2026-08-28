using System;
using System.IO;
using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// Locates and loads the upstream Endless Sky dataset once per process.
    /// The dataset is not vendored; it is found the same way the sim test
    /// suite finds it: $ENDLESS_SKY_DATA, else ../es-upstream/data, else
    /// external/endless-sky/data beside the project root.
    /// </summary>
    public static class EsData
    {
        private static GameData? _data;
        private static string? _dataPath;
        private static bool _searched;

        public static string? DataPath
        {
            get
            {
                if (_searched)
                {
                    return _dataPath;
                }

                _searched = true;
                // Fully qualified: Godot.Environment (the rendering environment) is in
                // scope here and collides with System.Environment.
                string? fromEnv = System.Environment.GetEnvironmentVariable("ENDLESS_SKY_DATA");
                string projectRoot = ProjectSettings.GlobalizePath("res://");
                // Order: explicit env override, then the in-repo reference clone so a
                // fresh checkout is self-contained (tools/get-data.ps1 populates it),
                // then a sibling es-upstream checkout as developer convenience.
                string?[] candidates =
                {
                    fromEnv,
                    Path.Combine(projectRoot, "external", "endless-sky", "data"),
                    Path.Combine(projectRoot, "..", "es-upstream", "data"),
                };
                foreach (string? candidate in candidates)
                {
                    if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate))
                    {
                        _dataPath = Path.GetFullPath(candidate);
                        break;
                    }
                }

                return _dataPath;
            }
        }

        /// <summary>The loaded universe, or null when no dataset is available.</summary>
        public static GameData? Universe
        {
            get
            {
                if (_data != null)
                {
                    return _data;
                }

                string? path = DataPath;
                if (path == null)
                {
                    return null;
                }

                var data = new GameData();
                data.LoadDirectory(path);
                GD.Print($"[data] loaded {data.Ships.Count} ships, {data.Outfits.Count} outfits, " +
                         $"{data.Systems.Count} systems from {path}");
                if (data.Diagnostics.Count > 0)
                {
                    GD.Print($"[data] {data.Diagnostics.Count} loader diagnostics (first: {data.Diagnostics[0]})");
                }

                _data = data;
                return _data;
            }
        }
    }
}
