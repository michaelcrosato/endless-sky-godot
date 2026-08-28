using System;
using System.Linq;
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
    }
}
