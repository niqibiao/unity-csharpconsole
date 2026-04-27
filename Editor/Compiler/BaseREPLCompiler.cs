using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Permissions;
using System.Text;
using System.Threading;
using dnlib.DotNet;
using Zh1Zh1.CSharpConsole.Interface;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnityEngine;

namespace Zh1Zh1.CSharpConsole.Editor.Compiler
{
    /// <summary>
    /// Roslyn script compiler.
    /// - Supports predefined usings and cached user usings.
    /// - Uses ScriptCompilation for REPL state across submissions.
    /// - Configures TopLevelBinderFlags so private member access can compile.
    /// - Post-processes the output assembly to inject SecurityPermission(SkipVerification) so Mono skips JIT verification.
    /// - Supports runtimeDllPath to replace editor assembly references with player assemblies.
    /// </summary>
    public class BaseREPLCompiler : IREPLCompiler, IREPLCompletionProvider
    {
        public const int MAX_SUBMISSION_ID = IREPLExecutor.MAX_SUBMISSION_ID;

        private readonly string m_AssemblyPrefix;
        private readonly bool m_CacheReferences;
        private readonly string m_DefaultDefines;
        private readonly string m_RuntimeDllPath;

        private int m_SubmissionId;
        private CSharpCompilation m_PreviousCompilation;
        private MetadataReference[] m_CachedReferences;

        private readonly static string[] s_DefaultUsings =
        {
            "using System;",
            "using UnityEngine;",
        };

        private readonly HashSet<string> m_CachedUsingLines = new(StringComparer.Ordinal);

        public BaseREPLCompiler(string assemblyPrefix, string defaultDefines, bool cacheReferences, string runtimeDllPath = null)
        {
            m_AssemblyPrefix = assemblyPrefix;
            m_DefaultDefines = defaultDefines ?? "";
            m_CacheReferences = cacheReferences;
            m_RuntimeDllPath = runtimeDllPath;
            ConsoleLog.Debug($"BaseREPLCompiler created: assemblyPrefix={m_AssemblyPrefix}, defaultDefines={m_DefaultDefines}, cacheReferences={m_CacheReferences}, runtimeDllPath={m_RuntimeDllPath}");
        }

        /// <summary>
        /// Compiles REPL code.
        /// </summary>
        /// <param name="code">User code.</param>
        /// <param name="defines">Preprocessor symbols separated by semicolons. Falls back to m_DefaultDefines when empty.</param>
        /// <param name="defaultUsing">Additional default using prefix.</param>
        public virtual (byte[] assemblyBytes, string scriptClass, string errorMsg) Compile(string code, string defines = null, string defaultUsing = null)
        {
            if (m_SubmissionId >= MAX_SUBMISSION_ID)
            {
                return (null, null, "Submission buffer is full");
            }

            var allDefineSymbols = ResolveDefineSymbols(defines);
            var parseOptions = new CSharpParseOptions(kind: SourceCodeKind.Script).WithPreprocessorSymbols(allDefineSymbols);

            var fullCode = BuildUsingPrefix(defaultUsing) + code;
            var tree = CSharpSyntaxTree.ParseText(fullCode, parseOptions);
            var root = (CompilationUnitSyntax)tree.GetRoot();

            CacheUsings(root);

            var deDupRoot = DeDupUsings(root);
            tree = CSharpSyntaxTree.Create(deDupRoot, parseOptions);

            if (IsOnlyUsings(deDupRoot))
            {
                return default;
            }

            var refs = GetReferences();

            var assemblyName = $"{m_AssemblyPrefix}{GetHashCode()}_{m_SubmissionId}";
            var scriptClassName = $"{m_AssemblyPrefix}{GetHashCode()}_{m_SubmissionId}";

            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Release)
                .WithMetadataImportOptions(MetadataImportOptions.All)
                .WithAllowUnsafe(true)
                .WithScriptClassName(scriptClassName);

            SetIgnoreAccessibility(options);

            var compilation = CSharpCompilation.CreateScriptCompilation(
                assemblyName,
                tree,
                refs,
                options,
                m_PreviousCompilation,
                typeof(object)
            );

            using var ms = new MemoryStream();
            var result = compilation.Emit(ms);
            if (!result.Success)
            {
                var errorsOnly = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
                var resultMsg = Enumerable.Aggregate(errorsOnly, "", (current, diag) => current + diag);
                return (null, null, resultMsg);
            }

            m_SubmissionId++;
            Volatile.Write(ref m_PreviousCompilation, compilation);

            var assemblyBytes = ms.ToArray();
            assemblyBytes = PostProcess(assemblyBytes);
            return (assemblyBytes, scriptClassName, null);
        }

        private static byte[] PostProcess(byte[] rawAssembly)
        {
            using var module = ModuleDefMD.Load(rawAssembly, new ModuleCreationOptions { TryToLoadPdbFromDisk = false });

            var namedArg = new CANamedArgument(
                false,
                module.CorLibTypes.Boolean,
                "SkipVerification",
                new CAArgument(module.CorLibTypes.Boolean, true));

            var attrType = module.Import(typeof(SecurityPermissionAttribute));
            var secDecl = new DeclSecurityUser(dnlib.DotNet.SecurityAction.RequestMinimum, new[]
            {
                new dnlib.DotNet.SecurityAttribute(attrType, new[] { namedArg })
            });

            module.Assembly.DeclSecurities.Add(secDecl);

            using var outMs = new MemoryStream();
            module.Write(outMs);
            return outMs.ToArray();
        }

        private void CacheUsings(CompilationUnitSyntax root)
        {
            lock (m_CachedUsingLines)
            {
                foreach (var u in root.Usings)
                {
                    var line = u.ToFullString().Trim();
                    if (!line.EndsWith(";", StringComparison.Ordinal))
                        line += ";";
                    m_CachedUsingLines.Add(line);
                }
            }
        }

        private static bool IsOnlyUsings(CompilationUnitSyntax root)
        {
            return root.Usings.Count > 0 && root.Members.Count == 0;
        }

        private static CompilationUnitSyntax DeDupUsings(CompilationUnitSyntax root)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var uniqueUsings = new List<UsingDirectiveSyntax>();
            foreach (var u in root.Usings)
            {
                var ns = u.Name?.ToString() ?? "";
                if (string.IsNullOrEmpty(ns) || seen.Contains(ns))
                    continue;
                seen.Add(ns);
                uniqueUsings.Add(u);
            }
            return root.WithUsings(SyntaxFactory.List(uniqueUsings));
        }

        private string BuildUsingPrefix(string extraUsings)
        {
            var sb = new StringBuilder();

            foreach (var u in m_CachedUsingLines)
                sb.AppendLine(u);

            if (!string.IsNullOrEmpty(extraUsings))
            {
                var extraUsingLines = extraUsings.Split("\n");
                foreach (var u in extraUsingLines)
                {
                    if (!m_CachedUsingLines.Contains(u))
                        sb.AppendLine(u);
                }
            }

            foreach (var u in s_DefaultUsings)
            {
                if (!m_CachedUsingLines.Contains(u))
                    sb.AppendLine(u);
            }

            return sb.ToString();
        }

        private MetadataReference[] GetReferences()
        {
            if (m_CacheReferences && m_CachedReferences != null)
            {
                return m_CachedReferences;
            }

            var refs = new List<MetadataReference>();
            var addedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. If runtimeDllPath is specified, collect DLLs from that directory for replacement.
            var customDlls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(m_RuntimeDllPath) && Directory.Exists(m_RuntimeDllPath))
            {
                foreach (var dll in Directory.GetFiles(m_RuntimeDllPath, "*.dll", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(dll);
                    customDlls.TryAdd(name, dll);
                }
            }

            // 2. Iterate over assemblies already loaded in the AppDomain.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location))
                    continue;

                var name = Path.GetFileNameWithoutExtension(asm.Location);

                // If runtimeDllPath is specified, skip assemblies already provided by the custom DLL set so the player version is used instead.
                if (!string.IsNullOrEmpty(m_RuntimeDllPath) && customDlls.ContainsKey(name))
                {
                    continue;
                }

                string dllPath = asm.Location;

                if (!addedNames.Contains(name))
                {
                    try
                    {
                        refs.Add(MetadataReference.CreateFromFile(dllPath));
                        addedNames.Add(name);
                    }
                    catch (Exception)
                    {
                        // Ignore assemblies that cannot be loaded.
                    }
                }
            }

            // 3. Add remaining DLLs from the custom directory, including player-only assemblies.
            foreach (var kvp in customDlls)
            {
                if (!addedNames.Contains(kvp.Key))
                {
                    try
                    {
                        refs.Add(MetadataReference.CreateFromFile(kvp.Value));
                        addedNames.Add(kvp.Key);
                    }
                    catch (Exception)
                    {
                        // Ignore assemblies that cannot be loaded.
                    }
                }
            }

            var result = refs.ToArray();
            if (m_CacheReferences)
            {
                m_CachedReferences = result;
            }

            return result;
        }

        // https://www.strathweb.com/2018/10/no-internalvisibleto-no-problem-bypassing-c-visibility-rules-with-roslyn/
        private static readonly PropertyInfo s_TopLevelBinderFlags =
            typeof(CSharpCompilationOptions).GetProperty("TopLevelBinderFlags", BindingFlags.Instance | BindingFlags.NonPublic);

        private static void SetIgnoreAccessibility(CSharpCompilationOptions options)
        {
            if (s_TopLevelBinderFlags != null)
            {
                s_TopLevelBinderFlags.SetValue(options, (uint)1 << 22);
            }
            else
            {
                ConsoleLog.Warning("TopLevelBinderFlags property not found on CSharpCompilationOptions — private member access will not work. " +
                    $"Roslyn version: {typeof(CSharpCompilationOptions).Assembly.GetName().Version}");
            }
        }

        private string[] ResolveDefineSymbols(string defines)
        {
            var str = string.IsNullOrEmpty(defines) ? m_DefaultDefines : defines;
            return str.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
        }

        /// <summary>
        /// Gets completion items.
        /// </summary>
        /// <param name="code">Code text.</param>
        /// <param name="cursorPosition">Cursor position.</param>
        /// <param name="defines">Preprocessor symbols.</param>
        /// <param name="defaultUsing">Default using prefix.</param>
        /// <returns>Completion items.</returns>
        public List<CompletionItem> GetCompletions(string code, int cursorPosition, string defines, string defaultUsing)
        {
            var allDefineSymbols = ResolveDefineSymbols(defines);
            var parseOptions = new CSharpParseOptions(kind: SourceCodeKind.Script)
                .WithPreprocessorSymbols(allDefineSymbols);

            string usingPrefix;
            lock (m_CachedUsingLines)
            {
                usingPrefix = BuildUsingPrefix(defaultUsing);
            }
            var fullCode = usingPrefix + code;
            var adjustedPosition = usingPrefix.Length + cursorPosition;

            if (adjustedPosition < 0) adjustedPosition = 0;
            if (adjustedPosition > fullCode.Length) adjustedPosition = fullCode.Length;

            var tree = CSharpSyntaxTree.ParseText(fullCode, parseOptions);

            var prevCompilation = Volatile.Read(ref m_PreviousCompilation);
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithMetadataImportOptions(MetadataImportOptions.All)
                .WithAllowUnsafe(true);
            SetIgnoreAccessibility(options);

            var compilation = CSharpCompilation.CreateScriptCompilation(
                "CompletionTemp",
                tree,
                GetReferences(),
                options,
                prevCompilation,
                typeof(object));

            var semanticModel = compilation.GetSemanticModel(tree);

            var root = tree.GetRoot();
            var token = root.FindToken(adjustedPosition);

            var memberAccess = token.Parent?.FirstAncestorOrSelf<MemberAccessExpressionSyntax>();
            if (memberAccess == null && adjustedPosition > 0)
            {
                token = root.FindToken(adjustedPosition - 1);
                memberAccess = token.Parent?.FirstAncestorOrSelf<MemberAccessExpressionSyntax>();
            }

            while (memberAccess?.Parent is MemberAccessExpressionSyntax outer
                   && adjustedPosition > outer.OperatorToken.SpanStart)
            {
                memberAccess = outer;
            }
            if (memberAccess != null && adjustedPosition > memberAccess.OperatorToken.SpanStart)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(memberAccess.Expression);

                if (symbolInfo.Symbol is INamedTypeSymbol namedType)
                {
                    var staticMembers = CollectAllTypeMembers(namedType)
                        .Where(m => m.IsStatic);
                    return BuildSortedCompletionItems(staticMembers);
                }

                if (symbolInfo.Symbol is INamespaceSymbol ns)
                {
                    return BuildSortedCompletionItems(ns.GetMembers());
                }

                var typeInfo = semanticModel.GetTypeInfo(memberAccess.Expression);
                var type = typeInfo.Type ?? typeInfo.ConvertedType;
                if (type != null)
                {
                    var lookupSymbols = semanticModel.LookupSymbols(adjustedPosition, type);
                    var allTypeMembers = CollectAllTypeMembers(type);
                    return BuildSortedCompletionItems(lookupSymbols.Concat(allTypeMembers));
                }
            }

            var symbols = semanticModel.LookupSymbols(adjustedPosition);
            return BuildSortedCompletionItems(symbols);
        }

        private static IEnumerable<ISymbol> CollectAllTypeMembers(ITypeSymbol type)
        {
            var current = type;
            while (current != null)
            {
                foreach (var member in current.GetMembers())
                {
                    yield return member;
                }
                current = current.BaseType;
            }
        }

        private static List<CompletionItem> BuildSortedCompletionItems(IEnumerable<ISymbol> symbols)
        {
            return symbols
                .Where(s => s.CanBeReferencedByName && !IsObsolete(s))
                .GroupBy(s => s.Name)
                .Select(g => g.OrderBy(s => GetAccessibilityPriority(s.DeclaredAccessibility)).First())
                .OrderBy(s => GetAccessibilityPriority(s.DeclaredAccessibility))
                .ThenBy(s => GetKindPriority(s.Kind))
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(ToCompletionItem)
                .ToList();
        }

        private static int GetAccessibilityPriority(Accessibility accessibility)
        {
            switch (accessibility)
            {
                case Accessibility.Public: return 0;
                case Accessibility.Internal: return 1;
                case Accessibility.ProtectedOrInternal: return 1;
                case Accessibility.Protected: return 2;
                case Accessibility.ProtectedAndInternal: return 2;
                case Accessibility.Private: return 3;
                default: return 4;
            }
        }

        private static int GetKindPriority(SymbolKind kind)
        {
            switch (kind)
            {
                case SymbolKind.Local: return 0;
                case SymbolKind.Parameter: return 0;
                case SymbolKind.Field: return 1;
                case SymbolKind.Property: return 1;
                case SymbolKind.Method: return 2;
                case SymbolKind.Event: return 3;
                case SymbolKind.NamedType: return 4;
                case SymbolKind.Namespace: return 5;
                default: return 6;
            }
        }

        private static bool IsObsolete(ISymbol symbol)
        {
            foreach (var attr in symbol.GetAttributes())
            {
                if (attr.AttributeClass != null
                    && attr.AttributeClass.Name == "ObsoleteAttribute"
                    && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "System")
                {
                    return true;
                }
            }
            return false;
        }

        private static CompletionItem ToCompletionItem(ISymbol symbol)
        {
            return new CompletionItem
            {
                Label = symbol.Name,
                Kind = symbol.Kind.ToString(),
                Detail = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                Accessibility = symbol.DeclaredAccessibility.ToString(),
            };
        }
    }
}
