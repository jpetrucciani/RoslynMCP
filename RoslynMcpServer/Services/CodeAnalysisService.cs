using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Models;

namespace RoslynMcpServer.Services
{
    public class CodeAnalysisService
    {
        private readonly ILogger<CodeAnalysisService> _logger;
        private readonly ConcurrentDictionary<string, Solution> _solutionCache = new();
        private readonly ConcurrentDictionary<string, MSBuildWorkspace> _workspaces = new();

        public CodeAnalysisService(ILogger<CodeAnalysisService> logger)
        {
            _logger = logger;
        }

        public async Task<Solution> GetSolutionAsync(string solutionPath)
        {
            var fullPath = Path.GetFullPath(solutionPath);
            if (_solutionCache.TryGetValue(fullPath, out var cachedSolution))
            {
                return cachedSolution;
            }

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Solution or project file not found.", fullPath);
            }

            var workspace = MSBuildWorkspace.Create();
            workspace.RegisterWorkspaceFailedHandler(args =>
            {
                _logger.LogWarning(
                    "MSBuild workspace diagnostic: {Diagnostic}",
                    args.Diagnostic.Message
                );
            });

            Solution solution;
            var extension = Path.GetExtension(fullPath);
            if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
            {
                solution = await workspace.OpenSolutionAsync(fullPath);
            }
            else if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var project = await workspace.OpenProjectAsync(fullPath);
                solution = project.Solution;
            }
            else
            {
                throw new NotSupportedException("Only .sln and .csproj files are supported.");
            }

            _workspaces[fullPath] = workspace;
            _solutionCache[fullPath] = solution;
            return solution;
        }

        public async Task<DependencyAnalysis> AnalyzeDependenciesAsync(
            string solutionPath,
            int maxDepth
        )
        {
            var solution = await GetSolutionAsync(solutionPath);
            var analysis = new DependencyAnalysis
            {
                ProjectName = Path.GetFileNameWithoutExtension(solutionPath),
            };
            var namespaceUsages = new Dictionary<string, NamespaceUsage>(StringComparer.Ordinal);
            var dependencies = new Dictionary<string, ProjectDependency>(StringComparer.Ordinal);

            foreach (var project in solution.Projects.Where(project => project.SupportsCompilation))
            {
                foreach (var projectReference in project.ProjectReferences)
                {
                    var referencedProject = solution.GetProject(projectReference.ProjectId);
                    AddDependency(
                        dependencies,
                        referencedProject?.Name ?? projectReference.ProjectId.Id.ToString(),
                        "ProjectReference"
                    );
                }

                foreach (var metadataReference in project.MetadataReferences)
                {
                    if (!string.IsNullOrEmpty(metadataReference.Display))
                    {
                        AddDependency(
                            dependencies,
                            Path.GetFileNameWithoutExtension(metadataReference.Display),
                            "MetadataReference"
                        );
                    }
                }

                var compilation = await project.GetCompilationAsync();
                if (compilation != null)
                {
                    var symbols = GetSymbols(compilation.Assembly.GlobalNamespace).ToList();
                    analysis.TotalSymbols += symbols.Count;
                    analysis.PublicSymbols += symbols.Count(symbol =>
                        symbol.DeclaredAccessibility == Accessibility.Public
                    );
                    analysis.InternalSymbols += symbols.Count(symbol =>
                        symbol.DeclaredAccessibility == Accessibility.Internal
                    );
                }

                foreach (var document in project.Documents)
                {
                    var root = await document.GetSyntaxRootAsync();
                    if (root == null)
                    {
                        continue;
                    }

                    foreach (
                        var usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                    )
                    {
                        var namespaceName = usingDirective.Name?.ToString();
                        if (!string.IsNullOrWhiteSpace(namespaceName))
                        {
                            AddNamespaceUsage(namespaceUsages, namespaceName);
                        }
                    }
                }
            }

            analysis.Dependencies = dependencies
                .Values.OrderByDescending(dependency => dependency.UsageCount)
                .ThenBy(dependency => dependency.Name)
                .Take(Math.Max(1, maxDepth) * 20)
                .ToList();
            analysis.NamespaceUsages = namespaceUsages
                .Values.OrderByDescending(namespaceUsage => namespaceUsage.UsageCount)
                .ThenBy(namespaceUsage => namespaceUsage.Namespace)
                .ToList();

            return analysis;
        }

        private static IEnumerable<ISymbol> GetSymbols(INamespaceOrTypeSymbol container)
        {
            foreach (var member in container.GetMembers())
            {
                yield return member;

                if (member is INamespaceOrTypeSymbol nestedContainer)
                {
                    foreach (var nestedMember in GetSymbols(nestedContainer))
                    {
                        yield return nestedMember;
                    }
                }
            }
        }

        private static void AddDependency(
            Dictionary<string, ProjectDependency> dependencies,
            string name,
            string type
        )
        {
            var key = type + ":" + name;
            if (!dependencies.TryGetValue(key, out var dependency))
            {
                dependency = new ProjectDependency
                {
                    Name = name,
                    Type = type,
                    Version = string.Empty,
                };
                dependencies[key] = dependency;
            }

            dependency.UsageCount++;
        }

        private static void AddNamespaceUsage(
            Dictionary<string, NamespaceUsage> namespaceUsages,
            string namespaceName
        )
        {
            if (!namespaceUsages.TryGetValue(namespaceName, out var namespaceUsage))
            {
                namespaceUsage = new NamespaceUsage { Namespace = namespaceName };
                namespaceUsages[namespaceName] = namespaceUsage;
            }

            namespaceUsage.UsageCount++;
        }
    }
}
