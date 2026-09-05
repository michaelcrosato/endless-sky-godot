using System;
using System.IO;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// The generated galaxy in <c>universe/</c> — the content the game actually plays.
    /// </summary>
    /// <remarks>
    /// The sibling of <see cref="UpstreamData"/>, and the distinction matters more than
    /// it looks: the suite checks upstream's data and the player flies this one, so a
    /// rule can be perfectly correct under test and wrong in the game because the two
    /// are reading different files. Anything asserting what a PLAYER will experience
    /// belongs here.
    /// </remarks>
    internal static class GeneratedUniverse
    {
        private static GameData? _cached;
        private static string? _root;

        /// <summary>Finds this checkout's generated data without borrowing a parent checkout's.</summary>
        internal static string Root
        {
            get
            {
                if (_root != null)
                    return _root;

                var directory = new DirectoryInfo(AppContext.BaseDirectory);
                while (directory != null)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "project.godot")))
                    {
                        string candidate = Path.Combine(directory.FullName, "universe");
                        if (File.Exists(Path.Combine(candidate, "systems.txt")))
                            return _root = candidate;
                        break;
                    }

                    directory = directory.Parent;
                }

                throw new AssertionException("Generated universe not found in this checkout. " +
                    "Run python tools/worldgen/worldgen.py.");
            }
        }

        /// <summary>The loaded galaxy, parsed once per run by the real loader.</summary>
        internal static GameData Instance
        {
            get
            {
                if (_cached != null)
                    return _cached;

                var data = new GameData();
                data.LoadDirectory(Root);
                _cached = data;
                return _cached;
            }
        }
    }
}
