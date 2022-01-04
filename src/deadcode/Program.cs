namespace RemoveDeadCode {
    using Microsoft.Build.Locator;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.CSharp.Extensions;
    using Microsoft.CodeAnalysis.Shared.Extensions;
    using Microsoft.CodeAnalysis.FindSymbols;
    using Microsoft.CodeAnalysis.MSBuild;
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.IO;
    using System.IO.Enumeration;
    using System.Linq;
    using System.Threading.Tasks;
    using System.Diagnostics;
    using Roslyn.Utilities;

    /// <summary>
    /// Shakes the symbols that are not referenced in the solution.
    /// All symbols will be scoped in a "#if ZOMBIE / #endif" block.
    ///
    /// Method symbols that are kept are
    ///    * Main/MainAsync
    ///    * xunit test methods
    ///    * Constructors (for DI)
    ///    * Part of a asp.net core controller or "Startup" class
    ///    * Part of a serializable or data contract class/record/
    ///      struct
    ///
    /// and all referenced symbols.
    ///
    /// Other symbols that are kept are
    ///    * Classes that contain static symbols (e.g. Program)
    ///    * Static classes that contain symbols (e.g. extension
    ///      methods)
    ///    * Classes that are serializable or have a data contract
    ///      attribute.
    ///
    /// and all referenced symbols.
    ///
    /// To keep code, refer to it from a unit test, ideally testing
    /// the code.
    ///
    /// Interface members must be referenced by callers, or else
    /// the interface member and all of its implementations are
    /// removed.
    ///
    /// Best to run on a git repo, then look at the diff to make
    /// sure all is ok, then commit.
    /// </summary>
    internal static class Program {

        private static async Task Main(string[] args) {

            var op = Op.Comment;
            var projectNames = new List<string>();
            var showHelp = false;
            var solutionName = string.Empty;
            var p = new Mono.Options.OptionSet() {
                { "p|proj=", "the projects to analyze.", v => projectNames.Add(v) },
                { "s|sln=", "the solution containing it.", v => solutionName = v },
                { "a|action", "Perform the following action", (Action<Op>)(v => op = v) },
                { "h|help", "show this message and exit", v => showHelp = v != null },
            };
            List<string> extra;
            try {
                extra = p.Parse(args);
                if (string.IsNullOrEmpty(solutionName)) {
                    var folder = Directory.GetCurrentDirectory();
                    solutionName = Directory.GetFiles(folder, "*.sln").FirstOrDefault();
                }
                if (string.IsNullOrEmpty(solutionName)) {
                    throw new Exception("Solution must be specified.");
                }
#if DEBUG
                op = Op.Show;
#endif
            }
            catch (Exception e) {
                Console.WriteLine(e.Message);
                showHelp = true;
            }
            if (showHelp) {
                p.WriteOptionDescriptions(Console.Out);
                return;
            }

            // Locate and register the default instance of MSBuild installed on this machine.
            // Note that this uses the target framework used to build this binary to determine
            // the max version of dotnet sdk and thus msbuild and build definitions to use.
            // Since this is a .net 6 framework app, .net 7 and higher sdk are not supported.
            // TODO: Provide the option to register a specific sdk from command line.
            var instance = MSBuildLocator.RegisterDefaults();

            // The test solution is copied to the output directory when you build this sample.
            var workspace = MSBuildWorkspace.Create(new Dictionary<string, string> {
                { "CheckForSystemRuntimeDependency", "true" }
            });
            workspace.LoadMetadataForReferencedProjects = true;
            workspace.SkipUnrecognizedProjects = false;

            // Open the solution within the workspace.
            await workspace.OpenSolutionAsync(solutionName, new Logger()).ConfigureAwait(false);
            var continueShaking = true;
            while (continueShaking) {
                continueShaking = false;
                var solution = workspace.CurrentSolution;
                if (workspace.Diagnostics.Count > 0) {
                    foreach (var diagnostic in workspace.Diagnostics) {
                        Console.WriteLine(diagnostic.Kind + ":" + diagnostic.Message);
                    }
                    Console.WriteLine("Failed to compile solution. Fix issues re-run.");
                    return;
                }

                var projectGraph = solution.GetProjectDependencyGraph();
                foreach (var projectId in (IEnumerable<ProjectId>)projectGraph.GetTopologicallySortedProjects()) {
                    var project = solution.GetProject(projectId);
                    var dependencies = projectGraph.GetProjectsThatThisProjectTransitivelyDependsOn(projectId)
                        .Select(p => solution.GetProject(p));

                    // Filter projects
                    if (projectNames.Count > 0 && !projectNames.Any(p =>
                            FileSystemName.MatchesSimpleExpression(p, project.FilePath, true))) {
                        continue;
                    }

                    foreach (var doc in project.Documents) {
                        // Get the semantic model
                        var root = await doc.GetSyntaxRootAsync().ConfigureAwait(false);
                        var model = await doc.GetSemanticModelAsync().ConfigureAwait(false);

                        var rewriter = new ReferenceRemover(solution, model, op);
                        var newRoot = rewriter.Visit(root);
                        if (rewriter.EmptyFile && Op.Remove == op) {
                            if (!doc.Name.Contains("Assembly")) {
                                Console.WriteLine("Removing unused file: " + doc.Name);
                                if (doc.FilePath != null) {
                                    File.Delete(doc.FilePath);
                                    solution = solution.RemoveDocument(doc.Id);
                                }
                            }
                        }
                        else {
                            if (rewriter.WasUpdated) {
                                solution = solution.WithDocumentSyntaxRoot(doc.Id, newRoot);
                            }
                            if (rewriter.NeedsReRun) {
                                continueShaking = true;
                            }
                        }
                    }
                }

                if (!workspace.TryApplyChanges(solution)) {
                    Console.WriteLine("Failed to apply changes to workspace!");
                    return;
                }
            }
        }


        internal class ReferenceRemover : CSharpSyntaxRewriter {

            public bool NeedsReRun { get; internal set; }
            public bool WasUpdated { get; internal set; }
            public bool EmptyFile { get; internal set; } = true;

            public ReferenceRemover(Solution newSolution, SemanticModel model, Op op) {
                _solution = newSolution;
                _model = model;
                _op = op;
            }

            public override SyntaxNode VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node) {
                return OrderUsings((BaseNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node));
            }

            public override SyntaxNode VisitNamespaceDeclaration(NamespaceDeclarationSyntax node) {
                return OrderUsings((BaseNamespaceDeclarationSyntax)base.VisitNamespaceDeclaration(node));
            }

            public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node) {
                var result = VisitClassDeclarationAsync(node).Result;
                return result == node ? base.VisitClassDeclaration(node) : result;
            }

            public override SyntaxNode VisitDelegateDeclaration(DelegateDeclarationSyntax node) {
                var result = VisitDelegateDeclarationAsync(node).Result;
                return result == node ? base.VisitDelegateDeclaration(node) : result;
            }

            public override SyntaxNode VisitEnumDeclaration(EnumDeclarationSyntax node) {
                var result = VisitEnumDeclarationAsync(node).Result;
                return result == node ? base.VisitEnumDeclaration(node) : result;
            }

            public override SyntaxNode VisitEventDeclaration(EventDeclarationSyntax node) {
                var result = VisitEventDeclarationAsync(node).Result;
                return result == node ? base.VisitEventDeclaration(node) : result;
            }

            public override SyntaxNode VisitFieldDeclaration(FieldDeclarationSyntax node) {
                var result = VisitFieldDeclarationAsync(node).Result;
                return result == node ? base.VisitFieldDeclaration(node) : result;
            }

            public override SyntaxNode VisitIndexerDeclaration(IndexerDeclarationSyntax node) {
                var result = VisitIndexerDeclarationAsync(node).Result;
                return result == node ? base.VisitIndexerDeclaration(node) : result;
            }

            public override SyntaxNode VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) {
                var result = VisitInterfaceDeclarationAsync(node).Result;
                return result == node ? base.VisitInterfaceDeclaration(node) : result;
            }

            public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node) {
                var result = VisitMethodDeclarationAsync(node).Result;
                return result == node ? base.VisitMethodDeclaration(node) : result;
            }

            public override SyntaxNode VisitPropertyDeclaration(PropertyDeclarationSyntax node) {
                var result = VisitPropertyDeclarationAsync(node).Result;
                return result == node ? base.VisitPropertyDeclaration(node) : result;
            }

            public override SyntaxNode VisitRecordDeclaration(RecordDeclarationSyntax node) {
                var result = VisitRecordDeclarationAsync(node).Result;
                return result == node ? base.VisitRecordDeclaration(node) : result;
            }

            public override SyntaxNode VisitStructDeclaration(StructDeclarationSyntax node) {
                var result = VisitStructDeclarationAsync(node).Result;
                return result == node ? base.VisitStructDeclaration(node) : result;
            }

            private async Task<SyntaxNode> VisitDelegateDeclarationAsync(DelegateDeclarationSyntax node) {
                var symbol = _model.GetDeclaredSymbol(node);
                return await VisitSyntaxNodeAsync(node, symbol).ConfigureAwait(false);
            }

            private async Task<SyntaxNode> VisitEventDeclarationAsync(EventDeclarationSyntax node) {
                var symbol = _model.GetDeclaredSymbol(node);
                return await VisitSyntaxNodeAsync(node, symbol).ConfigureAwait(false);
            }

            private async Task<SyntaxNode> VisitFieldDeclarationAsync(FieldDeclarationSyntax node) {
                var symbol = _model.GetDeclaredSymbol(node);
                return await VisitSyntaxNodeAsync(node, symbol).ConfigureAwait(false);
            }

            private async Task<SyntaxNode> VisitEnumDeclarationAsync(EnumDeclarationSyntax node) {
                var symbol = _model.GetDeclaredSymbol(node);
                return await VisitSyntaxNodeAsync(node, symbol).ConfigureAwait(false);
            }

            private async Task<SyntaxNode> VisitInterfaceDeclarationAsync(InterfaceDeclarationSyntax node) {
                var symbol = _model.GetDeclaredSymbol(node);
                return await VisitNamedTypeDeclarationAsync(node, symbol).ConfigureAwait(false);
            }

            private async Task<SyntaxNode> VisitRecordDeclarationAsync(RecordDeclarationSyntax node) {
                var symbol = _model.GetDeclaredSymbol(node);
                return await VisitNamedTypeDeclarationAsync(node, symbol).ConfigureAwait(false);
            }

            private async Task<SyntaxNode> VisitStructDeclarationAsync(StructDeclarationSyntax node) {
                var symbol = _model.GetDeclaredSymbol(node);
                return await VisitNamedTypeDeclarationAsync(node, symbol).ConfigureAwait(false);
            }

            private async Task<SyntaxNode> VisitClassDeclarationAsync(ClassDeclarationSyntax node) {
                var symbol = _model.GetDeclaredSymbol(node);
                return await VisitNamedTypeDeclarationAsync(node, symbol).ConfigureAwait(false);
            }

            private async Task<SyntaxNode> VisitNamedTypeDeclarationAsync(SyntaxNode node, INamedTypeSymbol symbol) {
                EmptyFile = false;
                if (symbol == null) {
                    return node;
                }

                if (symbol.IsAspNetEntryPoint()) {
                    return node;
                }

                // If any member is extension (not accessed through type) or test method (access by reflection)
                if (symbol.GetMembers().Any(m =>
                        m.IsExtensionMethod() ||
                        m.IsProgramEntryPoint() ||
                        m.IsTestMethod())) {
                    return node;
                }

                if (symbol.GetAttributes().Any()) {
                    return node;
                }

                // Filter references from symbol's constructor
                return await RemoveIfSymbolNotReferencedAsync(node, symbol).ConfigureAwait(false);
            }

            private async Task<SyntaxNode> VisitIndexerDeclarationAsync(IndexerDeclarationSyntax node) {
                var symbol = _model.GetDeclaredSymbol(node);
                return await VisitPropertyDeclarationAsync(node, symbol).ConfigureAwait(false);
            }

            private async Task<SyntaxNode> VisitPropertyDeclarationAsync(PropertyDeclarationSyntax node) {
                var symbol = _model.GetDeclaredSymbol(node);
                return await VisitPropertyDeclarationAsync(node, symbol).ConfigureAwait(false);
            }

            private async Task<SyntaxNode> VisitPropertyDeclarationAsync(SyntaxNode node, IPropertySymbol symbol) {
                EmptyFile = false;
                if (symbol == null) {
                    return node;
                }
                if (symbol.IsOverride) {
                    return node; // Overridden external
                }
                var containingType = symbol.ContainingType;
                if (containingType != null) {

                    if (containingType.TypeKind == TypeKind.Interface) {
                        // If only implementations
                        return await RemoveIfSymbolNotCalledAsync(node, symbol).ConfigureAwait(false);
                    }
                }
                if (symbol.IsPartOfDataContract()) {
                    return node;
                }
                // If it is implementing an interface
                if (symbol.ExplicitOrImplicitInterfaceImplementations().Any()) {
                    return node;
                }
                return await RemoveIfSymbolNotReferencedAsync(node, symbol).ConfigureAwait(false);
            }

            private async Task<SyntaxNode> VisitMethodDeclarationAsync(MethodDeclarationSyntax node) {
                EmptyFile = false;
                var symbol = _model.GetDeclaredSymbol(node);
                if (symbol == null) {
                    return node;
                }

                if (symbol.IsTestMethod()) {
                    return node;
                }

                // Test whether it is implicitly needed (Api or controller)
                var containingType = symbol.ContainingType;
                if (containingType != null) {

                    if (containingType.TypeKind == TypeKind.Interface) {
                        // If only implementations
                        return await RemoveIfSymbolNotCalledAsync(node, symbol).ConfigureAwait(false);
                    }

                    if (containingType.IsAspNetEntryPoint()) {
                        // test whether startup type is referenced
                        return node;
                    }
                }

                // check known exclusions
                if (symbol.IsStatic) {
                    if (symbol.IsProgramEntryPoint()) {
                        return node;
                    }
                }
                else {
                    // If it is implementing an interface
                    if (symbol.ExplicitOrImplicitInterfaceImplementations().Any()) {
                        return node;
                    }
                    if (symbol.IsOverride) {
                        return node; // Overridden external
                    }
                }
                return await RemoveIfSymbolNotReferencedAsync(node, symbol).ConfigureAwait(false);
            }

            private async Task<SyntaxNode> VisitSyntaxNodeAsync(SyntaxNode node, ISymbol symbol) {
                EmptyFile = false;
                if (symbol == null) {
                    return node;
                }
                return await RemoveIfSymbolNotReferencedAsync(node, symbol).ConfigureAwait(false);
            }

            private async Task<SyntaxNode> RemoveIfSymbolNotReferencedAsync(SyntaxNode node, ISymbol symbol,
                Func<ReferencedSymbol, bool> predicate = null) {
                EmptyFile = false;
                // Check if there are any references
                var allReferences = await SymbolFinder.FindReferencesAsync(symbol, _solution).ConfigureAwait(false);
                var references = allReferences.Where(r => r.Locations.Any());
                references = predicate != null ? references.Where(predicate) : references;
                if (references.Any()) {
                    return node;
                }
                return RemoveNode(node);
            }

            private async Task<SyntaxNode> RemoveIfSymbolNotCalledAsync(SyntaxNode node, ISymbol symbol) {
                // Check if there are any references
                EmptyFile = false;
                var references = await SymbolFinder.FindCallersAsync(symbol, _solution).ConfigureAwait(false);
                if (references.Any()) {
                    return node;
                }
                return RemoveNode(node);
            }

            private SyntaxNode OrderUsings(BaseNamespaceDeclarationSyntax namespaceDeclaration) {
                var old = new List<UsingDirectiveSyntax>(namespaceDeclaration.Usings);
                if (old.Count <= 1) {
                    return namespaceDeclaration;
                }
                var list = new List<UsingDirectiveSyntax>();
                var nsName = namespaceDeclaration.Name.ToString();
                while (!string.IsNullOrEmpty(nsName)) {

                    // Get everything starting with nsName
                    list.AddRange(old
                        .Where(e => e.Name.ToString().StartsWith(nsName))
                        .OrderBy(e => e.Name.ToString()));
                    old.RemoveAll(e => e.Name.ToString().StartsWith(nsName));

                    var idx = nsName.LastIndexOf('.');
                    if (idx == -1) {
                        break;
                    }
                    nsName = nsName[..idx];
                }
                list.AddRange(old.OrderBy(e => e.Name.ToString()));

                var finalUsings = new SyntaxList<UsingDirectiveSyntax>(list);
                var resultNamespace = namespaceDeclaration.WithUsings(finalUsings);
                WasUpdated = true;
                return resultNamespace;
            }

            private SyntaxNode RemoveNode(SyntaxNode node) {
                EmptyFile = false;
                switch (_op) {
                    case Op.Comment:
                        var commented = AddTrivia(node);
                        Console.WriteLine(commented.ToFullString());
                        NeedsReRun = WasUpdated = true;
                        return commented;
                    case Op.Remove:
                        NeedsReRun = WasUpdated = true;
                        Console.WriteLine(node.ToFullString());
                        return node.RemoveNodes(node.DescendantNodesAndSelf(), SyntaxRemoveOptions.KeepEndOfLine);
                }
                Console.WriteLine(node.ToFullString());
                return node;
            }

            internal static SyntaxNode AddTrivia(SyntaxNode node) {
                return node
                    .WithLeadingTrivia(
                        node.GetLeadingTrivia().Insert(0,
                            SyntaxFactory.Trivia(
                                SyntaxFactory.IfDirectiveTrivia(
                                    SyntaxFactory.IdentifierName("ZOMBIE"),
                                    true,
                                    false,
                                    false
                                )
                                .WithHashToken(
                                    SyntaxFactory.Token(SyntaxKind.HashToken)
                                )
                                .WithIfKeyword(
                                    SyntaxFactory.Token(
                                        SyntaxFactory.TriviaList(),
                                        SyntaxKind.IfKeyword,
                                        SyntaxFactory.TriviaList(
                                            SyntaxFactory.Space
                                        )
                                    )
                                )
                                .WithEndOfDirectiveToken(
                                    SyntaxFactory.Token(
                                        SyntaxFactory.TriviaList(),
                                        SyntaxKind.EndOfDirectiveToken,
                                        SyntaxFactory.TriviaList(
                                            SyntaxFactory.LineFeed
                                        )
                                    )
                                )
                            )
                        )
                    )
                    .WithTrailingTrivia(
                        node.GetTrailingTrivia().Add(
                            SyntaxFactory.Trivia(
                                SyntaxFactory.EndIfDirectiveTrivia(
                                    true
                                )
                                .WithEndOfDirectiveToken(
                                    SyntaxFactory.Token(
                                        SyntaxFactory.TriviaList(),
                                        SyntaxKind.EndOfDirectiveToken,
                                        SyntaxFactory.TriviaList(
                                            SyntaxFactory.LineFeed
                                        )
                                    )
                                )
                            )
                        )
                    );
            }

            private readonly Solution _solution;
            private readonly SemanticModel _model;
            private readonly Op _op;
        }

        private class Logger : IProgress<ProjectLoadProgress> {

            public void Report(ProjectLoadProgress value) {
                Console.WriteLine($"{value.Operation}: {value.FilePath} ({value.TargetFramework})");
            }
        }
    }

    public enum Op {
        Comment,
        Show,
        Remove
    }

    internal static class ISymbolExtensions {

        public static bool IsProgramEntryPoint(this ISymbol callingSymbol) {
            if (callingSymbol is not IMethodSymbol method || !method.IsStatic) {
                return false;
            }
            return
                string.Equals(method.Name, "MainAsync", StringComparison.Ordinal) ||
                string.Equals(method.Name, "Main", StringComparison.Ordinal);
        }

        public static bool IsAspNetEntryPoint(this ISymbol callingSymbol) {
            return string.Equals(callingSymbol.Name, "Startup", StringComparison.Ordinal) ||
                callingSymbol.GetAttributes()
                    .Any(a => a.AttributeClass.Name.Contains("Controller"));
        }

        public static bool IsTestMethod(this ISymbol callingSymbol) {
            if (callingSymbol is not IMethodSymbol method) {
                return false;
            }
            var attributeNames = new string[] {
                "Theory",
                "Fact",
                "SkippableFact",
                "SkippableTheory"
            };
            return method.GetAttributes()
                .Select(a => a.AttributeClass.Name)
                .Any(a => attributeNames.Any(n => a.StartsWith(n, StringComparison.Ordinal)));
        }

        public static bool IsExtensionMethod(this ISymbol callingSymbol) {
            if (callingSymbol is not IMethodSymbol method) {
                return false;
            }
            return method.IsExtensionMethod;
        }

        public static bool IsConstructor(this ISymbol callingSymbol) {
            if (callingSymbol is not IMethodSymbol method) {
                return false;
            }
            return method.IsConstructor();
        }

        public static bool IsPartOfDataContract(this ISymbol callingSymbol) {
            if (callingSymbol.ContainingType is not INamedTypeSymbol type) {
                return false;
            }
            var attributeNames = new string[] {
                "DataContract",
                "Serializable"
            };
            return type.GetAttributes()
                .Select(a => a.AttributeClass.Name)
                .Any(a => attributeNames.Any(n => a.StartsWith(n, StringComparison.Ordinal)));
        }

        public static ImmutableArray<ISymbol> ExplicitOrImplicitInterfaceImplementations(this ISymbol symbol) {
            if (symbol.Kind is not SymbolKind.Method and not SymbolKind.Property and not SymbolKind.Event) {
                return ImmutableArray<ISymbol>.Empty;
            }
            var containingType = symbol.ContainingType;
            var query = from iface in containingType.AllInterfaces
                        from interfaceMember in iface.GetMembers()
                        let impl = containingType.FindImplementationForInterfaceMember(interfaceMember)
                        where SymbolEqualityComparer.Default.Equals(symbol, impl)
                        select interfaceMember;
            return query.ToImmutableArray();
        }
    }
}
