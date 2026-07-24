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
    public class BaseREPLCompiler : IREPLCompiler, IREPLCompilerNoticeProvider, IREPLCompletionProvider
    {
        public const int MAX_SUBMISSION_ID = REPLExecutorLimits.MAX_SUBMISSION_ID;

        private readonly string m_AssemblyPrefix;
        private readonly bool m_CacheReferences;
        private readonly string m_DefaultDefines;
        private readonly string m_RuntimeDllPath;

        private int m_SubmissionId;
        private CSharpCompilation m_PreviousCompilation;
        private MetadataReference[] m_CachedReferences;
        private string m_PendingNotice;
        private bool m_HasReportedAccessibilityFallback;

        private readonly static string[] s_DefaultUsings =
        {
            "using System;",
            "using UnityEngine;",
        };

        private readonly HashSet<string> m_CachedUsingLines = new HashSet<string>(StringComparer.Ordinal);

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
            m_PendingNotice = null;
            if (m_SubmissionId >= MAX_SUBMISSION_ID)
            {
                return (null, null, "Submission buffer is full");
            }

            var allDefineSymbols = ResolveDefineSymbols(defines);
            var parseOptions = new CSharpParseOptions(kind: SourceCodeKind.Script).WithPreprocessorSymbols(allDefineSymbols);

            var fullCode = BuildUsingPrefix(defaultUsing) + code;
            var tree = CSharpSyntaxTree.ParseText(fullCode, parseOptions);
            var root = (CompilationUnitSyntax)tree.GetRoot();

            var deDupRoot = DeDupUsings(root);
            tree = CSharpSyntaxTree.Create(deDupRoot, parseOptions);

            if (IsOnlyUsings(deDupRoot))
            {
                // Validate before caching: a broken using cached here would poison every later submission.
                var usingErrorMsg = ValidateUsings(deDupRoot, parseOptions);
                if (usingErrorMsg != null)
                {
                    return (null, null, usingErrorMsg);
                }
                CacheUsings(root);
                return default;
            }

            var refs = GetReferences();

            var assemblyName = $"{m_AssemblyPrefix}{GetHashCode()}_{m_SubmissionId}";
            var scriptClassName = $"{m_AssemblyPrefix}{GetHashCode()}_{m_SubmissionId}";

            var compilation = CSharpCompilation.CreateScriptCompilation(
                assemblyName,
                tree,
                refs,
                BuildSubmissionOptions(scriptClassName, ignoreAccessibility: true),
                m_PreviousCompilation,
                typeof(object)
            );

            using var ms = new MemoryStream();
            var result = compilation.Emit(ms);
            string splitSubmissionHint = null;
            if (!result.Success && HasAmbiguityErrors(result.Diagnostics))
            {
                // Same-named internal types in two assemblies (e.g. DocumentFormat.OpenXml +
                // DocumentFormat.OpenXml.Framework polyfills) only become ambiguous (CS0433 etc.)
                // because accessibility is ignored; retry once with standard accessibility rules.
                var strictCompilation = CSharpCompilation.CreateScriptCompilation(
                    assemblyName,
                    tree,
                    refs,
                    BuildSubmissionOptions(scriptClassName, ignoreAccessibility: false),
                    m_PreviousCompilation,
                    typeof(object)
                );
                ms.SetLength(0);
                var strictResult = strictCompilation.Emit(ms);
                if (strictResult.Success)
                {
                    compilation = strictCompilation;
                    result = strictResult;
                    RecordAccessibilityFallbackNotice();
                }
                else
                {
                    splitSubmissionHint = BuildSplitSubmissionHint(result.Diagnostics, strictResult.Diagnostics);
                }
            }
            if (!result.Success)
            {
                var errorsOnly = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
                var resultMsg = Enumerable.Aggregate(errorsOnly, "", (current, diag) => current + diag);
                if (splitSubmissionHint != null)
                {
                    resultMsg += splitSubmissionHint;
                }
                return (null, null, resultMsg);
            }

            CacheUsings(root);
            m_SubmissionId++;
            Volatile.Write(ref m_PreviousCompilation, compilation);

            var assemblyBytes = ms.ToArray();
            assemblyBytes = PostProcess(assemblyBytes);
            return (assemblyBytes, scriptClassName, null);
        }

        public string ConsumeNotice()
        {
            var notice = m_PendingNotice;
            m_PendingNotice = null;
            return notice;
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
                    m_CachedUsingLines.Add(NormalizeUsingLine(u));
                }
            }
        }

        private static string NormalizeUsingLine(UsingDirectiveSyntax u)
        {
            var line = u.ToFullString().Trim();
            if (!line.EndsWith(";", StringComparison.Ordinal))
                line += ";";
            return line;
        }

        private static CSharpCompilationOptions BuildSubmissionOptions(string scriptClassName, bool ignoreAccessibility)
        {
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Release)
                .WithMetadataImportOptions(MetadataImportOptions.All)
                .WithAllowUnsafe(true)
                .WithScriptClassName(scriptClassName);
            if (ignoreAccessibility)
                SetIgnoreAccessibility(options);
            return options;
        }

        // Error ids that indicate same-named types/members colliding across assemblies —
        // typically internal polyfills that are only visible because accessibility is ignored.
        private static readonly string[] s_AmbiguityErrorIds = { "CS0433", "CS0104", "CS0229" };

        private static readonly string[] s_AccessibilityErrorIds = { "CS0122", "CS1540", "CS0271", "CS0272" };

        private static bool HasAmbiguityErrors(IEnumerable<Diagnostic> diagnostics)
        {
            return diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error && s_AmbiguityErrorIds.Contains(d.Id));
        }

        private void RecordAccessibilityFallbackNotice()
        {
            if (m_HasReportedAccessibilityFallback)
                return;

            m_HasReportedAccessibilityFallback = true;
            m_PendingNotice =
                "[REPL NOTICE]\n" +
                "Symbol conflict detected: this submission was recompiled with standard C# accessibility.\n" +
                "Non-public member access is unavailable in this submission.\n" +
                "Later submissions still try the REPL accessibility bypass first.";
        }

        /// <summary>
        /// Detects the split-fixable mixed failure: the accessibility-ignoring compile hit a
        /// cross-assembly ambiguity while the strict retry was blocked by non-public access at a
        /// different code location. Such a submission works when split in two, so return a hint;
        /// otherwise (same location, or strict failed for unrelated reasons) return null.
        /// </summary>
        private static string BuildSplitSubmissionHint(IEnumerable<Diagnostic> ignoreDiagnostics, IEnumerable<Diagnostic> strictDiagnostics)
        {
            var strictErrors = strictDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (strictErrors.Count == 0 || strictErrors.Any(d => s_AmbiguityErrorIds.Contains(d.Id)))
                return null;

            var ambiguitySpans = ignoreDiagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error && s_AmbiguityErrorIds.Contains(d.Id))
                .Select(d => d.Location.SourceSpan)
                .ToList();

            var accessibilityBlockedElsewhere = strictErrors.Any(d =>
                s_AccessibilityErrorIds.Contains(d.Id)
                && !ambiguitySpans.Any(span => span.IntersectsWith(d.Location.SourceSpan)));
            if (!accessibilityBlockedElsewhere)
                return null;

            return "\n\n[REPL ACTION REQUIRED]\n" +
                "Split this code into two REPL submissions:\n" +
                "  1. Submit the expression that uses the ambiguous type first.\n" +
                "  2. Submit the non-public member access separately afterward.\n" +
                "\n" +
                "Reason: ignoring accessibility exposed same-named types from multiple assemblies. " +
                "Standard accessibility resolved that ambiguity, but then correctly blocked the " +
                "non-public member access.";
        }

        /// <summary>
        /// Compiles the using directives alone (normalized with trailing semicolons) and returns
        /// the aggregated error message, or null when they are all valid.
        /// </summary>
        private string ValidateUsings(CompilationUnitSyntax root, CSharpParseOptions parseOptions)
        {
            var sb = new StringBuilder();
            foreach (var u in root.Usings)
                sb.AppendLine(NormalizeUsingLine(u));

            var tree = CSharpSyntaxTree.ParseText(sb.ToString(), parseOptions);

            var errors = GetUsingValidationErrors(tree, ignoreAccessibility: true);
            if (errors.Count > 0 && HasAmbiguityErrors(errors))
            {
                // Same fallback as Compile: e.g. "using static X;" where X is dual-defined
                // internal/public across assemblies is only ambiguous when accessibility is ignored.
                var strictErrors = GetUsingValidationErrors(tree, ignoreAccessibility: false);
                if (strictErrors.Count == 0)
                {
                    RecordAccessibilityFallbackNotice();
                    return null;
                }
            }

            var errorMsg = Enumerable.Aggregate(errors, "", (current, diag) => current + diag);
            return errorMsg.Length > 0 ? errorMsg : null;
        }

        private List<Diagnostic> GetUsingValidationErrors(SyntaxTree tree, bool ignoreAccessibility)
        {
            var compilation = CSharpCompilation.CreateScriptCompilation(
                "UsingValidation",
                tree,
                GetReferences(),
                BuildSubmissionOptions("UsingValidation", ignoreAccessibility),
                m_PreviousCompilation,
                typeof(object));

            return compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
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
                var extraUsingLines = extraUsings.Split('\n');
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
                    if (!customDlls.ContainsKey(name))
                        customDlls.Add(name, dll);
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
            var qualifiedName = token.Parent?.FirstAncestorOrSelf<QualifiedNameSyntax>();
            if (memberAccess == null && qualifiedName == null && adjustedPosition > 0)
            {
                token = root.FindToken(adjustedPosition - 1);
                memberAccess = token.Parent?.FirstAncestorOrSelf<MemberAccessExpressionSyntax>();
                qualifiedName = token.Parent?.FirstAncestorOrSelf<QualifiedNameSyntax>();
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

            // Qualified names appear outside expressions (e.g. "using UnityEngine." or type positions);
            // complete with members of the left namespace/type instead of falling back to scope lookup.
            while (qualifiedName?.Parent is QualifiedNameSyntax outerName
                   && adjustedPosition > outerName.DotToken.SpanStart)
            {
                qualifiedName = outerName;
            }
            if (qualifiedName != null && adjustedPosition > qualifiedName.DotToken.SpanStart)
            {
                var leftInfo = semanticModel.GetSymbolInfo(qualifiedName.Left);
                if (leftInfo.Symbol is INamespaceOrTypeSymbol namespaceOrType)
                {
                    return BuildSortedCompletionItems(namespaceOrType.GetMembers());
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
