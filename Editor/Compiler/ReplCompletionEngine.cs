// Shared completion engine used by both BaseREPLCompiler (HybridCLR / editor
// path) and LiteREPLCompiler (Lite path). Pure Roslyn-level work: given a
// CSharpCompilation already wired with the right script-chain context and a
// cursor position within the parsed tree, return the sorted CompletionItem
// list.
//
// Each caller is responsible for the state-shaped parts that differ between
// compilers (prefix building, reference resolution, previous-chain wiring)
// before invoking Lookup. Keeping this engine stateless preserves the
// single-flight invariant on the Lite path and avoids accidental coupling
// of the two compiler families.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Zh1Zh1.CSharpConsole.Interface;

namespace Zh1Zh1.CSharpConsole.Editor.Compiler
{
    internal static class ReplCompletionEngine
    {
        // Resolves completions at the given cursor position. The compilation
        // must already include the user's `code` (typically as the final tree)
        // and any usings prefix; adjustedPosition must point INTO that final
        // text. Caller-side prefix building is split out so each compiler can
        // use its own default-usings policy.
        public static List<CompletionItem> Lookup(
            CSharpCompilation compilation,
            SyntaxTree tree,
            int adjustedPosition)
        {
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
                    var staticMembers = CollectAllTypeMembers(namedType).Where(m => m.IsStatic);
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
