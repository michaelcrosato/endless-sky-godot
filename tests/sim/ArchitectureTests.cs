using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Reflection;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Guards the directive's third ground rule: rendering must not be mixed
    /// into data or simulation. Project structure already makes it a compile
    /// error, but structure is easy to erode -- a stray PackageReference to
    /// GodotSharp would silently re-open the door. These assertions fail loudly
    /// if that happens.
    /// </summary>
    [TestFixture]
    public class ArchitectureTests
    {
        private static Assembly DataAssembly => typeof(EndlessSky.Data.DataFile).Assembly;
        private static Assembly SimAssembly => typeof(EndlessSky.Sim.Ship).Assembly;

        private static string[] ReferencedAssemblyNames(Assembly assembly) =>
            assembly.GetReferencedAssemblies()
                    .Select(a => a.Name ?? string.Empty)
                    .ToArray();

        [Test]
        public void DataLayerDoesNotReferenceGodot()
        {
            var referenced = ReferencedAssemblyNames(DataAssembly);

            Assert.That(
                referenced.Any(n => n.StartsWith("Godot", StringComparison.OrdinalIgnoreCase)),
                Is.False,
                "EndlessSky.Data must stay engine-free. Referenced: " + string.Join(", ", referenced));
        }

        [Test]
        public void SimulationLayerDoesNotReferenceGodot()
        {
            var referenced = ReferencedAssemblyNames(SimAssembly);

            Assert.That(
                referenced.Any(n => n.StartsWith("Godot", StringComparison.OrdinalIgnoreCase)),
                Is.False,
                "EndlessSky.Sim must stay engine-free. Referenced: " + string.Join(", ", referenced));
        }

        [Test]
        public void SimulationDependsOnDataButNotTheOtherWayAround()
        {
            // Data is the lower layer: it parses files and knows nothing about
            // ships, systems or physics.
            Assert.That(ReferencedAssemblyNames(SimAssembly), Contains.Item("EndlessSky.Data"));
            Assert.That(ReferencedAssemblyNames(DataAssembly), Has.No.Member("EndlessSky.Sim"));
        }

        [Test]
        public void NoPublicSimulationTypeExposesAGodotType()
        {
            // Catches a leak through a transitive reference that the assembly-level
            // checks above would miss.
            var offenders = SimAssembly.GetExportedTypes()
                .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Select(m => new
                {
                    Member = m,
                    Type = m switch
                    {
                        PropertyInfo p => p.PropertyType,
                        FieldInfo f => f.FieldType,
                        MethodInfo mi => mi.ReturnType,
                        _ => null
                    }
                })
                .Where(x => x.Type?.Namespace?.StartsWith("Godot", StringComparison.Ordinal) == true)
                .Select(x => $"{x.Member.DeclaringType?.Name}.{x.Member.Name}")
                .ToArray();

            Assert.That(offenders, Is.Empty,
                "Simulation types must not surface Godot types: " + string.Join(", ", offenders));
        }

        /// <summary>
        /// Public simulation types that nothing outside the tests uses yet, with the
        /// reason each is still here.
        /// </summary>
        /// <remarks>
        /// This list is the point of the test below, not an exception to it. Every
        /// entry is a system that is built and covered and that the player cannot
        /// reach, which is the exact shape of defect the repository audit found nine
        /// times over: a green suite, because the suite tests the library, and a game
        /// that had run behind it. Deleting an entry as it gets wired up is the
        /// intended direction of travel; adding one should feel like a decision.
        /// </remarks>
        private static readonly Dictionary<string, string> UnreachedOnPurpose = new()
        {
            ["CaptureOdds"] =
                "Boarding is not reachable from the cockpit, so nothing captures a "
                + "crippled hull. Tracked in docs/MILESTONES.md.",
        };

        [Test]
        public void EveryPublicSimulationTypeIsUsedBySomethingOtherThanATest()
        {
            // The suite cannot see this class of defect by construction: a subsystem
            // can be correct, covered and called by nobody, and every test still
            // passes. Reachability is the missing assertion.
            //
            // This is a coarse net on purpose. It works at TYPE level over the source
            // text, so it catches a whole subsystem nothing reaches -- which is what
            // actually happened, nine times over -- but not a single orphaned method on
            // a type the game otherwise uses. Catching those needs a real call graph;
            // this catches the ones that cost the most, for the least machinery.
            string root = RepositoryRoot();
            Assume.That(root, Is.Not.Null, "run from inside the repository");

            var sources = SourceFiles(root!);

            var orphans = new List<string>();

            foreach (Type type in SimAssembly.GetExportedTypes())
            {
                // Nested and compiler-generated types are reached through their parent.
                if (type.IsNested || type.Name.Contains('<'))
                    continue;

                if (UnreachedOnPurpose.ContainsKey(type.Name))
                    continue;

                bool usedElsewhere = sources.Any(entry =>
                    !DefinesType(entry.Key, type.Name) &&
                    MentionsType(entry.Value, type.Name));

                if (!usedElsewhere)
                    orphans.Add(type.Name);
            }

            Assert.That(orphans, Is.Empty,
                "These simulation types are used by nothing but tests, which means the "
                + "game cannot reach them. Wire them up, or add them to "
                + "UnreachedOnPurpose with the reason: " + string.Join(", ", orphans));
        }

        [Test]
        public void TheUnreachedListDoesNotOutliveWhatItExcuses()
        {
            // An excuse list that keeps naming things somebody has since wired up stops
            // being an inventory and becomes noise — so it fails both ways: for a type
            // that no longer exists, and for one that is now reached after all.
            var names = SimAssembly.GetExportedTypes().Select(t => t.Name).ToHashSet();
            var gone = UnreachedOnPurpose.Keys.Where(n => !names.Contains(n)).ToArray();

            Assert.That(gone, Is.Empty,
                "UnreachedOnPurpose names types that no longer exist: " + string.Join(", ", gone));

            string root = RepositoryRoot();
            Assume.That(root, Is.Not.Null, "run from inside the repository");

            var reached = UnreachedOnPurpose.Keys
                .Where(name => SourceFiles(root!).Any(entry =>
                    !DefinesType(entry.Key, name) && MentionsType(entry.Value, name)))
                .ToArray();

            Assert.That(reached, Is.Empty,
                "These are reached now, so the excuse should go with the fix: "
                + string.Join(", ", reached));
        }

        /// <summary>Every C# source file the shipped game and its libraries are built from.</summary>
        private static Dictionary<string, string> SourceFiles(string root) => Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "libs"), "*.cs",
                                             SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToDictionary(f => f, File.ReadAllText);

        /// <summary>Whether this file is where the type is declared.</summary>
        private static bool DefinesType(string path, string typeName) =>
            string.Equals(Path.GetFileNameWithoutExtension(path), typeName,
                          StringComparison.Ordinal)
            || Path.GetFileNameWithoutExtension(path)
                   .StartsWith(typeName + ".", StringComparison.Ordinal);

        /// <summary>A whole-word mention, so "Ship" does not match "ShipView".</summary>
        private static bool MentionsType(string text, string typeName) =>
            Regex.IsMatch(text, @"\b" + Regex.Escape(typeName) + @"\b");

        /// <summary>Walks up from the test binary to the directory holding project.godot.</summary>
        private static string? RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "project.godot")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            return null;
        }
    }
}
