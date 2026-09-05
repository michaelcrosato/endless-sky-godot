using System;
using System.Collections.Generic;
using System.IO;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Shared access to the real Endless Sky dataset for tests that assert against
    /// upstream content rather than hand-written fixtures.
    /// </summary>
    /// <remarks>
    /// The dataset is not vendored into this repository (external/ is gitignored), so
    /// it is located at run time. Point <c>ENDLESS_SKY_DATA</c> at an upstream
    /// checkout's <c>data</c> directory, run <c>tools/get-data.ps1</c> to populate
    /// <c>external/endless-sky</c>, or keep a checkout beside the project as
    /// <c>../es-upstream</c>. Missing data fails dependent tests with setup instructions.
    ///
    /// Loading the whole dataset takes a moment, so read-only content tests share one
    /// parsed instance. Tests that change the universe or restore a save must load
    /// their own GameData from RequiredPath to avoid changing later tests' fixtures.
    /// </remarks>
    internal static class UpstreamData
    {
        private static GameData _cached;
        private static string _dataPath;

        /// <summary>The loaded dataset. Fails if required upstream data is unavailable.</summary>
        internal static GameData Instance
        {
            get
            {
                if (_cached != null)
                    return _cached;

                string path = RequiredPath;

                var data = new GameData();
                data.LoadDirectory(path);
                _cached = data;
                return data;
            }
        }

        /// <summary>
        /// The upstream <c>data</c> directory, failing the calling test when it is absent.
        /// </summary>
        /// <remarks>
        /// Tests that read the dataset off disk rather than through <see cref="Instance"/>
        /// use the same requirement as tests that load the parsed dataset. A run with
        /// no fixture cannot establish parity, even if its self-contained tests pass.
        /// </remarks>
        internal static string RequiredPath =>
            Path ?? throw new AssertionException(
                "Upstream Endless Sky data not found. Run tools/get-data.ps1, set " +
                "ENDLESS_SKY_DATA, or clone endless-sky beside this project as ../es-upstream.");

        /// <summary>The upstream <c>data</c> directory, or null when it cannot be found.</summary>
        internal static string Path
        {
            get
            {
                if (_dataPath != null)
                    return _dataPath.Length == 0 ? null : _dataPath;

                var candidates = new List<string>();

                string fromEnv = Environment.GetEnvironmentVariable("ENDLESS_SKY_DATA");
                if (!string.IsNullOrEmpty(fromEnv))
                {
                    string explicitPath = System.IO.Path.GetFullPath(fromEnv);
                    if (!File.Exists(System.IO.Path.Combine(explicitPath, "commodities.txt")))
                        throw new AssertionException("ENDLESS_SKY_DATA must name an upstream data " +
                            "directory containing commodities.txt: " + explicitPath);
                    return _dataPath = explicitPath;
                }

                // Walk up from the test binary to the Godot project root (the directory
                // holding project.godot), then probe the documented checkout locations.
                string projectRoot = AppContext.BaseDirectory;
                while (projectRoot != null &&
                       !File.Exists(System.IO.Path.Combine(projectRoot, "project.godot")))
                {
                    projectRoot = System.IO.Path.GetDirectoryName(projectRoot);
                }

                if (projectRoot != null)
                {
                    // In-repo checkout first: it makes a fresh clone self-contained.
                    candidates.Add(System.IO.Path.Combine(projectRoot, "external", "endless-sky", "data"));
                    candidates.Add(System.IO.Path.Combine(projectRoot, "..", "es-upstream", "data"));
                }

                foreach (string candidate in candidates)
                {
                    string full = System.IO.Path.GetFullPath(candidate);
                    if (File.Exists(System.IO.Path.Combine(full, "commodities.txt")))
                    {
                        _dataPath = full;
                        return full;
                    }
                }

                _dataPath = string.Empty;
                return null;
            }
        }
    }
}
