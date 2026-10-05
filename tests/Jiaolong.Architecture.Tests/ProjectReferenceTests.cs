using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jiaolong.Architecture.Tests;

[TestClass]
public sealed class ProjectReferenceTests
{
    [TestMethod]
    public void ControlCenter_references_shared_logic_without_a_hardware_transport() =>
        ProjectGraphAssert.ReferencesAre("Jiaolong.ControlCenter", "Jiaolong.Contracts", "Jiaolong.Diagnostics", "Jiaolong.Automation");

    [TestMethod]
    public void Dependency_graph_has_no_cycles() => ProjectGraphAssert.IsAcyclic();

    private static class ProjectGraphAssert
    {
        public static void ReferencesAre(string projectName, params string[] expectedReferences)
        {
            var projects = LoadProjects();
            var project = projects.SingleOrDefault(candidate => candidate.Name == projectName);
            Assert.IsNotNull(project, $"Solution does not contain project '{projectName}'.");

            var actualReferences = ReadProjectReferences(project.Path);
            CollectionAssert.AreEquivalent(expectedReferences, actualReferences.ToArray());
        }

        public static void IsAcyclic()
        {
            var projects = LoadProjects();
            Assert.IsNotEmpty(projects, "Solution project graph has not been authored.");

            var graph = projects.ToDictionary(
                project => project.Name,
                project => ReadProjectReferences(project.Path));

            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var projectName in graph.Keys)
            {
                Assert.IsFalse(HasCycle(projectName, graph, visiting, visited),
                    $"Dependency graph contains a cycle at '{projectName}'.");
            }
        }

        private static bool HasCycle(
            string projectName,
            IReadOnlyDictionary<string, IReadOnlyList<string>> graph,
            ISet<string> visiting,
            ISet<string> visited)
        {
            if (visiting.Contains(projectName))
            {
                return true;
            }

            if (!visited.Add(projectName))
            {
                return false;
            }

            visiting.Add(projectName);
            foreach (var dependency in graph[projectName])
            {
                if (graph.ContainsKey(dependency) && HasCycle(dependency, graph, visiting, visited))
                {
                    return true;
                }
            }

            visiting.Remove(projectName);
            return false;
        }

        private static List<ProjectInfo> LoadProjects()
        {
            var root = FindRepositoryRoot();
            var solution = XDocument.Load(Path.Combine(root, "Jiaolong.ControlCenter.slnx"));
            return solution.Root?
                .Descendants("Project")
                .Select(element =>
                {
                    var relativePath = element.Attribute("Path")?.Value
                        ?? throw new AssertFailedException("Solution project is missing Path.");
                    var path = Path.GetFullPath(Path.Combine(root, relativePath));
                    return new ProjectInfo(Path.GetFileNameWithoutExtension(path), path);
                })
                .ToList() ?? [];
        }

        private static IReadOnlyList<string> ReadProjectReferences(string projectPath)
        {
            var project = XDocument.Load(projectPath);
            return project.Descendants("ProjectReference")
                .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")?.Value
                    ?? throw new AssertFailedException("Project reference is missing Include.")))
                .ToArray();
        }

        private static string FindRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Jiaolong.ControlCenter.slnx")))
                {
                    return directory.FullName;
                }
            }

            Assert.Fail("Repository root with Jiaolong.ControlCenter.slnx was not found.");
            return string.Empty;
        }

        private sealed record ProjectInfo(string Name, string Path);
    }
}
