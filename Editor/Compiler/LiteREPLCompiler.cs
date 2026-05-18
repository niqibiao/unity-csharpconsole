// Lite-mode editor compiler — takes user C# text, runs Roslyn to AST/semantic,
// translates to a System.Linq.Expressions tree, returns LambdaExpression that
// can be (a) compiled in-editor via .Compile() for editor-target diagnostics,
// or (b) handed to LiteWireWriter to produce binary body bytes for Player.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnityEngine;

namespace Zh1Zh1.CSharpConsole.Lite
{

    // ===================================================================
    // LiteREPLCompiler: per-REPL-instance state. Owns Roslyn compilation chain and
    // the runtime slot store.
    // ===================================================================
    public sealed class LiteREPLCompiler : ILiteCompiler
    {
        public readonly Dictionary<string, object> Slots = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Type> SlotTypes = new(StringComparer.Ordinal);

        IDictionary<string, object> ILiteCompiler.Slots => Slots;

        private CSharpCompilation m_Previous;
        private MetadataReference[] m_References;
        private int m_Counter;

        // Default usings stitched onto every submission so REPL ergonomics
        // match what users expect (e.g. `arr.Where(...)` without `using System.Linq;`,
        // `GameObject.Find(...)` without `using UnityEngine;`). This set is a
        // superset of BaseREPLCompiler's defaults — System.Linq + Collections.Generic
        // are added because Lite submissions tend to lean on LINQ more heavily
        // (no HybridCLR fallback means users write more in-line expression work).
        private const string s_DefaultUsings =
            "using System;\nusing System.Linq;\nusing System.Collections.Generic;\nusing UnityEngine;\n";

        public Delegate Compile(string code)
        {
            var lambda = CompileToLambda(code);
            return lambda.Compile(preferInterpretation: true);
        }

        // Returns the BCL LambdaExpression before .Compile() so DTO serializers
        // can inspect the tree. Commits translator state on success.
        public Expression<Func<object>> CompileToLambda(string code, string defaultUsing = "")
        {
            var refs = m_References ??= BuildReferences();
            var prefix = s_DefaultUsings + (string.IsNullOrEmpty(defaultUsing) ? "" : (defaultUsing.EndsWith("\n") ? defaultUsing : defaultUsing + "\n"));
            var tree = CSharpSyntaxTree.ParseText(prefix + code, new CSharpParseOptions(kind: SourceCodeKind.Script));
            var asmName = "LiteCompile_" + (++m_Counter) + "_" + Guid.NewGuid().ToString("N");

            // Match the HybridCLR-path BaseREPLCompiler accessibility relaxation
            // so private / internal field access works (e.g. `go.m_InstanceID`
            // on UnityEngine.Object). Without this, Roslyn rejects the symbol
            // as "no accessible definition" even when the field exists.
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithMetadataImportOptions(MetadataImportOptions.All);
            SetIgnoreAccessibility(options);

            var compilation = CSharpCompilation.CreateScriptCompilation(
                asmName,
                syntaxTree: tree,
                references: refs,
                options: options,
                previousScriptCompilation: m_Previous,
                returnType: typeof(object));

            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
            if (errors.Count > 0)
                throw new InvalidOperationException("Roslyn errors: " + string.Join("; ", errors.Select(d => d.GetMessage())));

            var translator = new RoslynToExpressionTranslator(this, compilation.GetSemanticModel(tree));
            var root = (CompilationUnitSyntax)tree.GetRoot();
            // Collect raw global statements first so `using var` declarations
            // can wrap subsequent statements via BuildStatementListExpr.
            var globalStatements = new List<StatementSyntax>();
            var stmts = new List<Expression>();
            foreach (var member in root.Members)
            {
                switch (member)
                {
                    case GlobalStatementSyntax gs:
                        globalStatements.Add(gs.Statement);
                        break;
                    case FieldDeclarationSyntax fd:
                        stmts.AddRange(translator.VisitVariableDeclaration(fd.Declaration));
                        break;
                    case ClassDeclarationSyntax cd:
                        throw new LiteCompilerException("E_LITE_TYPE_DECL",
                            $"Lite mode does not support top-level `class` declaration ('{cd.Identifier.ValueText}'). Move the class into a regular .cs file in your project, or switch to Full mode (HybridCLR).");
                    case StructDeclarationSyntax sd:
                        throw new LiteCompilerException("E_LITE_TYPE_DECL",
                            $"Lite mode does not support top-level `struct` declaration ('{sd.Identifier.ValueText}'). Move the struct into a regular .cs file, or switch to Full mode.");
                    case RecordDeclarationSyntax rd:
                        throw new LiteCompilerException("E_LITE_TYPE_DECL",
                            $"Lite mode does not support top-level `record` declaration ('{rd.Identifier.ValueText}'). Move the record into a regular .cs file, or switch to Full mode.");
                    case InterfaceDeclarationSyntax ifd:
                        throw new LiteCompilerException("E_LITE_TYPE_DECL",
                            $"Lite mode does not support top-level `interface` declaration ('{ifd.Identifier.ValueText}'). Move the interface into a regular .cs file, or switch to Full mode.");
                    case EnumDeclarationSyntax ed:
                        throw new LiteCompilerException("E_LITE_TYPE_DECL",
                            $"Lite mode does not support top-level `enum` declaration ('{ed.Identifier.ValueText}'). Move the enum into a regular .cs file, or switch to Full mode.");
                    case DelegateDeclarationSyntax dd:
                        throw new LiteCompilerException("E_LITE_TYPE_DECL",
                            $"Lite mode does not support top-level `delegate` declaration ('{dd.Identifier.ValueText}'). Use `System.Func<>`/`System.Action<>` or switch to Full mode.");
                    case MethodDeclarationSyntax md:
                        throw new LiteCompilerException("E_LITE_METHOD_DECL",
                            $"Lite mode does not support top-level method declaration ('{md.Identifier.ValueText}'). Use a lambda assigned to a local: 'System.Func<...> {md.Identifier.ValueText} = (...) => ...;', or switch to Full mode.");
                    default:
                        throw new NotSupportedException($"Top-level {member.Kind()} not supported in Lite mode");
                }
            }
            if (globalStatements.Count > 0)
            {
                var listExpr = translator.BuildStatementListExpr(globalStatements, 0, keepLastValue: true);
                stmts.Add(listExpr);
            }

            Expression body;
            if (stmts.Count == 0)
            {
                body = Expression.Constant(null, typeof(object));
            }
            else
            {
                var last = stmts[stmts.Count - 1];
                if (last.Type == typeof(void))
                {
                    stmts.Add(Expression.Constant(null, typeof(object)));
                }
                else if (last.Type != typeof(object))
                {
                    stmts[stmts.Count - 1] = Expression.Convert(last, typeof(object));
                }
                // Out-vars introduced by inline `out int n` syntax during this
                // submission must be declared at the body Block so their scope
                // covers all statements (per C# scoping rules they extend to the
                // enclosing block — for top-level script that is the lambda body).
                body = translator.SubmissionOutVars.Count == 0
                    ? Expression.Block(typeof(object), stmts)
                    : Expression.Block(typeof(object), translator.SubmissionOutVars, stmts);
            }

            var lambda = Expression.Lambda<Func<object>>(body);

            // Only commit translator-staged side effects (type table additions) and
            // advance the Roslyn chain after build succeeded (no fail-fast thrown).
            translator.Commit();
            m_Previous = compilation;
            return lambda;
        }

        private static MetadataReference[] BuildReferences()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !string.IsNullOrEmpty(SafeLocation(a)))
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
                .ToArray();
        }

        private static string SafeLocation(Assembly a)
        {
            try { return a.Location; } catch { return null; }
        }

        // Roslyn does not expose an "ignore accessibility" option, but the
        // internal TopLevelBinderFlags carries an IgnoreAccessibility bit at
        // position 1<<22. Mirrors BaseREPLCompiler.
        // Reference: https://www.strathweb.com/2018/10/no-internalvisibleto-no-problem-bypassing-c-visibility-rules-with-roslyn/
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
                ConsoleLog.Warning("TopLevelBinderFlags property not found on CSharpCompilationOptions — Lite private member access will not work. " +
                    $"Roslyn version: {typeof(CSharpCompilationOptions).Assembly.GetName().Version}");
            }
        }
    }

    // ===================================================================
    // RoslynToExpressionTranslator: per-submission. Reads/writes LiteREPLCompiler.Slots via Expression,
    // updates LiteREPLCompiler.SlotTypes for fail-fast on next submission. Staged
    // changes are applied via Commit() only after successful compile.
    // ===================================================================
    internal sealed class RoslynToExpressionTranslator
    {
        private readonly LiteREPLCompiler m_Session;
        private readonly SemanticModel m_Model;
        private readonly Stack<Dictionary<string, ParameterExpression>> m_LambdaScopes = new();
        private readonly Stack<(LabelTarget brk, LabelTarget cont)> m_LoopLabels = new();
        private LabelTarget m_ReturnLabel;
        private Type m_ReturnType;
        private readonly ConstantExpression m_SlotsExpr;
        private readonly Dictionary<string, Type> m_PendingSlotTypes = new(StringComparer.Ordinal);

        // Out-vars declared inline by `out int n` syntax during this submission.
        // They live in m_SubmissionScope (lookup) and m_SubmissionOutVars (ordered
        // list for body Block.Variables) — both populated in BindArguments and
        // drained by LiteREPLCompiler.CompileToLambda after translation.
        private readonly Dictionary<string, ParameterExpression> m_SubmissionScope =
            new Dictionary<string, ParameterExpression>(StringComparer.Ordinal);
        private readonly List<ParameterExpression> m_SubmissionOutVars = new List<ParameterExpression>();
        public IReadOnlyList<ParameterExpression> SubmissionOutVars => m_SubmissionOutVars;

        private static readonly MethodInfo s_DictGet =
            typeof(Dictionary<string, object>).GetMethod("get_Item", new[] { typeof(string) });
        private static readonly MethodInfo s_DictSet =
            typeof(Dictionary<string, object>).GetMethod("set_Item", new[] { typeof(string), typeof(object) });

        private static readonly SymbolDisplayFormat s_TypeNameFormat = new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.None);

        public RoslynToExpressionTranslator(LiteREPLCompiler s, SemanticModel m)
        {
            m_Session = s;
            m_Model = m;
            m_SlotsExpr = Expression.Constant(s.Slots, typeof(Dictionary<string, object>));
            // Push the submission-level scope as the outermost layer so out-vars
            // declared by inline `out int n` syntax are visible across the whole
            // submission (their C# scope per spec extends to the enclosing block).
            m_LambdaScopes.Push(m_SubmissionScope);
        }

        public void Commit()
        {
            foreach (var kv in m_PendingSlotTypes) m_Session.SlotTypes[kv.Key] = kv.Value;
        }

        // ---- Lite-mode deadlock fail-fast helpers ----
        // The four forbidden patterns are documented in §7.2 of the feasibility
        // doc: `await`, `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`. On
        // Unity's main thread (with its single-threaded SynchronizationContext)
        // any of these can deadlock when the awaited continuation needs the
        // main thread to resume.
        private static bool IsTaskLike(ITypeSymbol t)
        {
            if (t == null) return false;
            var name = t.OriginalDefinition.ToDisplayString();
            return name == "System.Threading.Tasks.Task"
                || name == "System.Threading.Tasks.Task<TResult>"
                || name == "System.Threading.Tasks.ValueTask"
                || name == "System.Threading.Tasks.ValueTask<TResult>";
        }

        private static bool IsAwaiterLike(ITypeSymbol t)
        {
            if (t == null) return false;
            var name = t.OriginalDefinition.ToDisplayString();
            return name == "System.Runtime.CompilerServices.TaskAwaiter"
                || name == "System.Runtime.CompilerServices.TaskAwaiter<TResult>"
                || name == "System.Runtime.CompilerServices.ValueTaskAwaiter"
                || name == "System.Runtime.CompilerServices.ValueTaskAwaiter<TResult>"
                || name == "System.Runtime.CompilerServices.ConfiguredTaskAwaitable.ConfiguredTaskAwaiter"
                || name == "System.Runtime.CompilerServices.ConfiguredTaskAwaitable<TResult>.ConfiguredTaskAwaiter";
        }

        private static void RejectDeadlock(string pattern, string hint)
        {
            throw new LiteCompilerException(
                "E_LITE_DEADLOCK_FORBIDDEN",
                $"Lite mode forbids `{pattern}` — risks main-thread deadlock. {hint}");
        }

        private Type LookupSlotType(string name)
        {
            if (m_PendingSlotTypes.TryGetValue(name, out var t)) return t;
            if (m_Session.SlotTypes.TryGetValue(name, out t)) return t;
            return null;
        }

        // ---------- statements ----------
        public IEnumerable<Expression> VisitStatement(StatementSyntax stmt)
        {
            switch (stmt)
            {
                case LocalDeclarationStatementSyntax local:
                    // using-declaration is folded by VisitBlock so it can wrap
                    // the *remaining* statements of the enclosing block in a
                    // try-finally. Hitting it here means it appeared in a
                    // position where folding isn't possible.
                    if (local.UsingKeyword.Kind() == SyntaxKind.UsingKeyword)
                        throw new NotSupportedException("`using var` declaration not at block top-level not supported in Lite mode");
                    // ref local: `ref int r = ref x;` — Expression API has no
                    // ByRef local representation; rewrite to a normal local copy.
                    if (local.Declaration.Type is RefTypeSyntax)
                        throw new LiteCompilerException("E_LITE_REF_LOCAL",
                            "Lite mode does not support `ref` local declarations. Use a normal local copy and write back: 'var local = x; ... ; x = local;', or switch to Full mode.");
                    // dynamic local: `dynamic x = ...;` — DLR call-site binding
                    // is not representable in the Expression API.
                    var declTypeSym = m_Model.GetTypeInfo(local.Declaration.Type).Type;
                    if (declTypeSym != null && declTypeSym.TypeKind == TypeKind.Dynamic)
                        throw new LiteCompilerException("E_LITE_DYNAMIC",
                            "Lite mode does not support `dynamic` typing (DLR call-sites are not representable in the Expression API). Use a concrete type, or switch to Full mode.");
                    // Shadowing: a nested-block `var x = ...` whose name matches a
                    // session slot from a prior submission would silently overwrite
                    // the slot in this Lite mode's simplified model. Reject early so the
                    // user gets a clear rename suggestion instead of confusing state.
                    foreach (var v in local.Declaration.Variables)
                    {
                        var n = v.Identifier.ValueText;
                        if (m_Session.SlotTypes.ContainsKey(n) || m_PendingSlotTypes.ContainsKey(n))
                            throw new LiteCompilerException(
                                "E_SESSION_SHADOWING",
                                $"Local '{n}' inside a nested block shadows a session-scoped variable of the same name. Rename the local to avoid ambiguity.");
                    }
                    return VisitVariableDeclaration(local.Declaration);
                case ExpressionStatementSyntax exprStmt:
                    return new[] { VisitExpression(exprStmt.Expression, targetType: null) };
                case IfStatementSyntax ifs:
                    return new[] { VisitIf(ifs) };
                case WhileStatementSyntax ws:
                    return new[] { VisitWhile(ws) };
                case DoStatementSyntax ds:
                    return new[] { VisitDoWhile(ds) };
                case SwitchStatementSyntax swStmt:
                    return new[] { VisitSwitchStatement(swStmt) };
                case ForStatementSyntax fs:
                    return new[] { VisitFor(fs) };
                case ForEachStatementSyntax fe:
                    return new[] { VisitForEach(fe) };
                case TryStatementSyntax ts:
                    return new[] { VisitTry(ts) };
                case UsingStatementSyntax us:
                    return new[] { VisitUsing(us) };
                case LockStatementSyntax ls:
                    return new[] { VisitLock(ls) };
                case CheckedStatementSyntax ck:
                    // Checked-arithmetic semantics not switched in Lite mode; emit body
                    // as a normal block. Documented limitation.
                    return new[] { (Expression)VisitBlock(ck.Block) };
                case LabeledStatementSyntax labelled:
                    return new[] { VisitLabeledStatement(labelled) };
                case GotoStatementSyntax gt:
                    return new[] { VisitGoto(gt) };
                case EmptyStatementSyntax:
                    return new[] { (Expression)Expression.Empty() };
                case YieldStatementSyntax _:
                    throw new LiteCompilerException("E_LITE_ITERATOR",
                        "Lite mode does not support iterators (`yield return`/`yield break`). Materialize the sequence with `Enumerable.Range(...)`, `.Select(...)`, or a `List<T>` build-up, or switch to Full mode.");
                case LocalFunctionStatementSyntax lfs:
                    throw new LiteCompilerException("E_LITE_METHOD_DECL",
                        $"Lite mode does not support local function declaration ('{lfs.Identifier.ValueText}'). Use a lambda assigned to a local: 'System.Func<...> {lfs.Identifier.ValueText} = (...) => ...;', or switch to Full mode.");
                case UnsafeStatementSyntax _:
                    throw new LiteCompilerException("E_LITE_UNSAFE",
                        "Lite mode does not support `unsafe` blocks (Expression API has no pointer / addressof / stackalloc representation). Rewrite using managed primitives, or switch to Full mode.");
                case ThrowStatementSyntax thr:
                    return new[] { Expression.Throw(VisitExpression(thr.Expression, typeof(Exception))) };
                case BreakStatementSyntax:
                    if (m_LoopLabels.Count == 0) throw new InvalidOperationException("break outside loop");
                    return new[] { (Expression)Expression.Goto(m_LoopLabels.Peek().brk) };
                case ContinueStatementSyntax:
                    if (m_LoopLabels.Count == 0) throw new InvalidOperationException("continue outside loop");
                    return new[] { (Expression)Expression.Goto(m_LoopLabels.Peek().cont) };
                case ReturnStatementSyntax ret:
                    if (m_ReturnLabel == null)
                        throw new InvalidOperationException("return outside lambda body block");
                    return new[] {
                        ret.Expression == null
                            ? Expression.Return(m_ReturnLabel)
                            : Expression.Return(m_ReturnLabel, VisitExpression(ret.Expression, m_ReturnType))
                    };
                case BlockSyntax block:
                    return new[] { VisitBlock(block) };
                default:
                    throw new NotSupportedException($"Statement {stmt.Kind()} not supported in Lite mode");
            }
        }

        private Expression VisitIf(IfStatementSyntax ifs)
        {
            var test = VisitExpression(ifs.Condition, typeof(bool));
            var thenExpr = StatementToExpression(ifs.Statement);
            if (ifs.Else == null)
                return Expression.IfThen(test, ToVoid(thenExpr));
            var elseExpr = StatementToExpression(ifs.Else.Statement);
            return Expression.IfThenElse(test, ToVoid(thenExpr), ToVoid(elseExpr));
        }

        private Expression VisitWhile(WhileStatementSyntax ws)
        {
            var breakLabel = Expression.Label("while_exit");
            var continueLabel = Expression.Label("while_cont");
            var test = VisitExpression(ws.Condition, typeof(bool));
            m_LoopLabels.Push((breakLabel, continueLabel));
            try
            {
                var body = StatementToExpression(ws.Statement);
                return Expression.Loop(
                    Expression.IfThenElse(
                        test,
                        Expression.Block(typeof(void),
                            ToVoid(body),
                            Expression.Label(continueLabel)),
                        Expression.Break(breakLabel)),
                    breakLabel);
            }
            finally { m_LoopLabels.Pop(); }
        }

        private Expression VisitBlock(BlockSyntax block)
        {
            return BuildStatementListExpr(block.Statements, 0, keepLastValue: false);
        }

        // Folds a sequence of statements into one Expression.
        //   keepLastValue=true: the last ExpressionStatement's value bubbles up
        //     as the result; the resulting Block is typed accordingly.
        //   keepLastValue=false: void block, used for statement-position bodies.
        // Handles `using var x = ...` by wrapping the remainder in try-finally,
        // preserving keepLastValue across the wrap.
        public Expression BuildStatementListExpr(IReadOnlyList<StatementSyntax> stmts, int start, bool keepLastValue)
        {
            var inner = new List<Expression>();
            Type tailType = typeof(void);

            for (int i = start; i < stmts.Count; i++)
            {
                var s = stmts[i];
                bool isLast = (i == stmts.Count - 1);

                if (s is LocalDeclarationStatementSyntax local
                    && local.UsingKeyword.Kind() == SyntaxKind.UsingKeyword)
                {
                    var locals = new List<ParameterExpression>();
                    var initStmts = new List<Expression>();
                    var disposables = new List<ParameterExpression>();
                    var scope = new Dictionary<string, ParameterExpression>(StringComparer.Ordinal);

                    var declType = local.Declaration.Type.IsVar
                        ? null
                        : ResolveTypeSymbol(m_Model.GetTypeInfo(local.Declaration.Type).Type);
                    foreach (var v in local.Declaration.Variables)
                    {
                        if (v.Initializer == null)
                            throw new InvalidOperationException($"using local '{v.Identifier.ValueText}' needs initializer");
                        var initExpr = VisitExpression(v.Initializer.Value, declType);
                        var t = declType ?? initExpr.Type;
                        var p = Expression.Parameter(t, v.Identifier.ValueText);
                        locals.Add(p);
                        scope[v.Identifier.ValueText] = p;
                        initStmts.Add(Expression.Assign(p, initExpr));
                        disposables.Add(p);
                    }

                    m_LambdaScopes.Push(scope);
                    try
                    {
                        var rest = BuildStatementListExpr(stmts, i + 1, keepLastValue);
                        var finallyExprs = new List<Expression>();
                        foreach (var d in disposables)
                        {
                            var dispTmp = Expression.Parameter(typeof(IDisposable), "__d_" + d.Name);
                            finallyExprs.Add(Expression.Block(
                                new[] { dispTmp },
                                Expression.Assign(dispTmp, Expression.TypeAs(d, typeof(IDisposable))),
                                Expression.IfThen(
                                    Expression.NotEqual(dispTmp, Expression.Constant(null, typeof(IDisposable))),
                                    Expression.Call(dispTmp, typeof(IDisposable).GetMethod("Dispose")))));
                        }
                        Expression finallyBlock = finallyExprs.Count == 1
                            ? finallyExprs[0]
                            : (Expression)Expression.Block(typeof(void), finallyExprs);
                        Expression tryFin = keepLastValue && rest.Type != typeof(void)
                            ? Expression.TryFinally(rest, finallyBlock)
                            : Expression.TryFinally(ToVoid(rest), finallyBlock);
                        inner.AddRange(initStmts);
                        inner.Add(tryFin);
                        tailType = tryFin.Type;
                        if (tailType == typeof(void))
                            return Expression.Block(typeof(void), locals, inner.Select(ToVoid));
                        // Coerce all but the last to void to match Block type
                        var seq = new List<Expression>();
                        for (int j = 0; j < inner.Count - 1; j++) seq.Add(ToVoid(inner[j]));
                        seq.Add(inner[inner.Count - 1]);
                        return Expression.Block(tailType, locals, seq);
                    }
                    finally { m_LambdaScopes.Pop(); }
                }
                else if (isLast && keepLastValue && s is ExpressionStatementSyntax exprStmt)
                {
                    var e = VisitExpression(exprStmt.Expression, null);
                    inner.Add(e);
                    tailType = e.Type;
                }
                else
                {
                    inner.AddRange(VisitStatement(s));
                }
            }

            if (inner.Count == 0) return Expression.Empty();
            if (tailType == typeof(void))
                return Expression.Block(typeof(void), inner.Select(ToVoid));
            var coerced = new List<Expression>();
            for (int j = 0; j < inner.Count - 1; j++) coerced.Add(ToVoid(inner[j]));
            coerced.Add(inner[inner.Count - 1]);
            return Expression.Block(tailType, coerced);
        }

        private Expression StatementToExpression(StatementSyntax s)
        {
            var list = VisitStatement(s).ToArray();
            if (list.Length == 0) return Expression.Empty();
            if (list.Length == 1) return list[0];
            return Expression.Block(list);
        }

        // Wraps an expression into a void-typed block so that branches of
        // IfThenElse / loop bodies can be uniformly typed (Expression.IfThen
        // and Expression.Loop expect Void unless we want a value).
        private static Expression ToVoid(Expression e)
        {
            if (e.Type == typeof(void)) return e;
            return Expression.Block(typeof(void), e);
        }

        public IEnumerable<Expression> VisitVariableDeclaration(VariableDeclarationSyntax decl)
        {
            var assigns = new List<Expression>();
            Type declaredType = ResolveDeclaredType(decl);
            foreach (var v in decl.Variables)
            {
                Type slotType = declaredType;
                Expression init = null;
                if (v.Initializer != null)
                {
                    init = VisitExpression(v.Initializer.Value, targetType: slotType);
                    if (slotType == null) slotType = init.Type;
                }
                if (slotType == null)
                    throw new InvalidOperationException($"Cannot infer type for '{v.Identifier.ValueText}'");

                var name = v.Identifier.ValueText;
                RegisterPendingSlot(name, slotType);

                if (init != null)
                {
                    if (init.Type != slotType) init = Expression.Convert(init, slotType);
                    var boxed = slotType.IsValueType
                        ? (Expression)Expression.Convert(init, typeof(object))
                        : init;
                    assigns.Add(Expression.Call(m_SlotsExpr, s_DictSet, Expression.Constant(name), boxed));
                }
                else
                {
                    // Uninitialized declaration `int x;` — store default(T) so
                    // later reads don't throw KeyNotFoundException.
                    var defVal = slotType.IsValueType
                        ? (Expression)Expression.Convert(Expression.Default(slotType), typeof(object))
                        : Expression.Constant(null, typeof(object));
                    assigns.Add(Expression.Call(m_SlotsExpr, s_DictSet, Expression.Constant(name), defVal));
                }
            }
            return assigns;
        }

        private void RegisterPendingSlot(string name, Type type)
        {
            // Pending = already declared earlier in THIS submission. Tolerate
            // same-type redeclaration only inside one submission to allow forms
            // like `(int a, int b) = (1, 2)` and per-statement repeats; Roslyn
            // itself catches real duplicates within one submission.
            if (m_PendingSlotTypes.TryGetValue(name, out var pendingT))
            {
                if (pendingT != type)
                    throw new LiteCompilerException(
                        "E_SESSION_REDECLARE_TYPE_MISMATCH",
                        $"Slot '{name}' was being declared as {pendingT.Name} earlier in this submission, cannot redeclare as {type.Name}.");
                return;
            }
            // Committed = declared in a PREVIOUS submission.
            if (m_Session.SlotTypes.TryGetValue(name, out var committedT))
            {
                if (committedT != type)
                    throw new LiteCompilerException(
                        "E_SESSION_REDECLARE_TYPE_MISMATCH",
                        $"Slot '{name}' already declared as {committedT.Name} in a previous submission, cannot redeclare as {type.Name}. " +
                        $"Lite mode forbids type-shift redeclaration; Full mode (HybridCLR) supports it.");
                throw new LiteCompilerException(
                    "E_SESSION_REDECLARE_DUPLICATE",
                    $"Slot '{name}' already declared in a previous submission; redeclaration is not allowed in Lite mode. Use '{name} = ...' to assign instead.");
            }
            m_PendingSlotTypes[name] = type;
        }

        // ---- session-slot reference predicate, used by ref/out + mutation guards ----
        private bool IsSessionSlotRoot(ExpressionSyntax e)
        {
            // Walk to the leftmost identifier (`a.b.c.d` → `a`).
            ExpressionSyntax cur = e;
            while (cur is MemberAccessExpressionSyntax m) cur = m.Expression;
            if (!(cur is IdentifierNameSyntax id)) return false;
            var name = id.Identifier.ValueText;
            // Lambda-scoped name wins per C# scoping — that's not a session slot.
            foreach (var scope in m_LambdaScopes)
                if (scope.ContainsKey(name)) return false;
            return LookupSlotType(name) != null;
        }

        private Type SessionSlotRootType(ExpressionSyntax e)
        {
            ExpressionSyntax cur = e;
            while (cur is MemberAccessExpressionSyntax m) cur = m.Expression;
            if (!(cur is IdentifierNameSyntax id)) return null;
            var name = id.Identifier.ValueText;
            foreach (var scope in m_LambdaScopes)
                if (scope.ContainsKey(name)) return null;
            return LookupSlotType(name);
        }

        // ---------- expressions ----------
        private Expression VisitExpression(ExpressionSyntax expr, Type targetType)
        {
            switch (expr)
            {
                case AwaitExpressionSyntax _:
                    RejectDeadlock("await",
                        "Use a Func/callback pattern, or run the submission off the main thread via Task.Run and read the result in a later submission.");
                    return null;
                case LiteralExpressionSyntax lit:
                    if (lit.IsKind(SyntaxKind.DefaultLiteralExpression))
                    {
                        if (targetType != null) return Expression.Default(targetType);
                        var convType = m_Model.GetTypeInfo(lit).ConvertedType;
                        if (convType != null) return Expression.Default(ResolveTypeSymbol(convType));
                        throw new InvalidOperationException("'default' literal needs a target type context");
                    }
                    return VisitLiteral(lit);
                case BinaryExpressionSyntax bin: return VisitBinary(bin);
                case IdentifierNameSyntax id: return VisitIdentifier(id);
                case ParenthesizedExpressionSyntax paren: return VisitExpression(paren.Expression, targetType);
                case SimpleLambdaExpressionSyntax lam: return VisitLambda(lam, targetType);
                case InvocationExpressionSyntax inv: return VisitInvocation(inv);
                case MemberAccessExpressionSyntax mae: return VisitMemberAccess(mae);
                case ObjectCreationExpressionSyntax obj: return VisitObjectCreation(obj);
                case CastExpressionSyntax cast: return VisitCast(cast);
                case ConditionalExpressionSyntax cond: return VisitConditional(cond, targetType);
                case AssignmentExpressionSyntax assign: return VisitAssignment(assign);
                case PrefixUnaryExpressionSyntax pre: return VisitPrefixUnary(pre);
                case ElementAccessExpressionSyntax elem: return VisitElementAccess(elem);
                case ArrayCreationExpressionSyntax arr: return VisitArrayCreation(arr);
                case InterpolatedStringExpressionSyntax interp: return VisitInterpolated(interp);
                case ParenthesizedLambdaExpressionSyntax plam: return VisitLambdaParen(plam, targetType);
                case ConditionalAccessExpressionSyntax cae: return VisitConditionalAccess(cae);
                case SwitchExpressionSyntax sw: return VisitSwitchExpression(sw);
                case PostfixUnaryExpressionSyntax post: return VisitPostfixUnary(post);
                case TypeOfExpressionSyntax tof:
                    return Expression.Constant(ResolveTypeFromSyntax(tof.Type), typeof(Type));
                case TupleExpressionSyntax tup: return VisitTuple(tup);
                case IsPatternExpressionSyntax isp: return VisitIsPattern(isp);
                case DefaultExpressionSyntax def:
                    return Expression.Default(ResolveTypeFromSyntax(def.Type));
                case RangeExpressionSyntax range: return VisitRange(range);
                case CheckedExpressionSyntax checkedExpr:
                    // checked(expr) / unchecked(expr): Lite mode treats as passthrough.
                    // Arithmetic overflow semantics aren't switched at the
                    // expression-tree level. Documented limitation.
                    return VisitExpression(checkedExpr.Expression, targetType);
                case QueryExpressionSyntax q: return VisitQueryExpression(q);
                default:
                    throw new NotSupportedException($"Expression {expr.Kind()} not supported in Lite mode");
            }
        }

        private Expression VisitIdentifier(IdentifierNameSyntax id)
        {
            var name = id.Identifier.ValueText;
            // Lambda-scoped param wins over slot (correct C# scoping).
            foreach (var scope in m_LambdaScopes)
                if (scope.TryGetValue(name, out var p)) return p;

            var t = LookupSlotType(name);
            if (t != null)
            {
                Expression read = Expression.Call(m_SlotsExpr, s_DictGet, Expression.Constant(name));
                return Expression.Convert(read, t);
            }
            throw new InvalidOperationException($"Identifier '{name}' not in any scope or slot table");
        }

        private static Expression VisitLiteral(LiteralExpressionSyntax lit)
        {
            if (lit.IsKind(SyntaxKind.NumericLiteralExpression))
            {
                var v = lit.Token.Value;
                if (v is int i) return Expression.Constant(i, typeof(int));
                if (v is long l) return Expression.Constant(l, typeof(long));
                if (v is double d) return Expression.Constant(d, typeof(double));
                if (v is float f) return Expression.Constant(f, typeof(float));
            }
            if (lit.IsKind(SyntaxKind.StringLiteralExpression))
                return Expression.Constant((string)lit.Token.Value, typeof(string));
            if (lit.IsKind(SyntaxKind.TrueLiteralExpression)) return Expression.Constant(true, typeof(bool));
            if (lit.IsKind(SyntaxKind.FalseLiteralExpression)) return Expression.Constant(false, typeof(bool));
            if (lit.IsKind(SyntaxKind.NullLiteralExpression)) return Expression.Constant(null, typeof(object));
            if (lit.IsKind(SyntaxKind.CharacterLiteralExpression))
                return Expression.Constant((char)lit.Token.Value, typeof(char));
            throw new NotSupportedException($"Literal {lit.Token.Value?.GetType().Name ?? "null"} not supported");
        }

        private Expression VisitBinary(BinaryExpressionSyntax bin)
        {
            // is/as: RHS is a TypeSyntax, not a value expression — must be
            // handled before recursively visiting bin.Right.
            if (bin.IsKind(SyntaxKind.IsExpression))
                return Expression.TypeIs(VisitExpression(bin.Left, null), ResolveTypeFromSyntax(bin.Right));
            if (bin.IsKind(SyntaxKind.AsExpression))
                return Expression.TypeAs(VisitExpression(bin.Left, null), ResolveTypeFromSyntax(bin.Right));

            var left = VisitExpression(bin.Left, null);
            var right = VisitExpression(bin.Right, null);
            // Nullable lifting: BCL's Expression.X(l, r) factories reject when
            // one side is T? and the other is T (e.g. `int? a > 3`). Lift the
            // non-nullable side to its Nullable<T> wrapper so BCL picks the
            // lifted overload. Same-type and same-nullable cases pass through
            // unchanged.
            NormalizeNullableOperands(ref left, ref right);
            switch (bin.Kind())
            {
                case SyntaxKind.AddExpression: return Expression.Add(left, right);
                case SyntaxKind.SubtractExpression: return Expression.Subtract(left, right);
                case SyntaxKind.MultiplyExpression: return Expression.Multiply(left, right);
                case SyntaxKind.DivideExpression: return Expression.Divide(left, right);
                case SyntaxKind.ModuloExpression: return Expression.Modulo(left, right);
                case SyntaxKind.GreaterThanExpression: return Expression.GreaterThan(left, right);
                case SyntaxKind.LessThanExpression: return Expression.LessThan(left, right);
                case SyntaxKind.GreaterThanOrEqualExpression: return Expression.GreaterThanOrEqual(left, right);
                case SyntaxKind.LessThanOrEqualExpression: return Expression.LessThanOrEqual(left, right);
                case SyntaxKind.EqualsExpression:
                    if (IsValueTuple(left.Type) && IsValueTuple(right.Type))
                        return LowerTupleEquality(left, right, equal: true);
                    return Expression.Equal(left, right);
                case SyntaxKind.NotEqualsExpression:
                    if (IsValueTuple(left.Type) && IsValueTuple(right.Type))
                        return LowerTupleEquality(left, right, equal: false);
                    return Expression.NotEqual(left, right);
                case SyntaxKind.LogicalAndExpression: return Expression.AndAlso(left, right);
                case SyntaxKind.LogicalOrExpression: return Expression.OrElse(left, right);
                case SyntaxKind.CoalesceExpression: return Expression.Coalesce(left, right);
                case SyntaxKind.BitwiseAndExpression: return Expression.And(left, right);
                case SyntaxKind.BitwiseOrExpression: return Expression.Or(left, right);
                case SyntaxKind.ExclusiveOrExpression: return Expression.ExclusiveOr(left, right);
                case SyntaxKind.LeftShiftExpression: return Expression.LeftShift(left, right);
                case SyntaxKind.RightShiftExpression: return Expression.RightShift(left, right);
                default:
                    throw new NotSupportedException($"Binary {bin.Kind()} not supported");
            }
        }

        private static void NormalizeNullableOperands(ref Expression left, ref Expression right)
        {
            if (left.Type == right.Type) return;
            var leftUnder = Nullable.GetUnderlyingType(left.Type);
            var rightUnder = Nullable.GetUnderlyingType(right.Type);
            if (leftUnder != null && rightUnder == null && leftUnder == right.Type)
                right = Expression.Convert(right, left.Type);
            else if (rightUnder != null && leftUnder == null && rightUnder == left.Type)
                left = Expression.Convert(left, right.Type);
        }

        // C# 7.3+ tuple `==`/`!=` lowering. Roslyn semantics: element-wise
        // comparison via `Equal`/`AndAlso` (Equal) or `Not(AndAlso(...Equal...))`
        // (NotEqual). BCL's Expression.Equal does NOT auto-lift to per-element
        // comparison for ValueTuple, so the translator must lower explicitly.
        // Nested ValueTuple element types recurse; for arity ≥ 8 the trailing
        // `Rest` field (itself a ValueTuple) is also recursive.
        private static bool IsValueTuple(Type t)
        {
            if (t == null || !t.IsGenericType) return false;
            var def = t.GetGenericTypeDefinition();
            return def == typeof(ValueTuple<>)
                || def == typeof(ValueTuple<,>)
                || def == typeof(ValueTuple<,,>)
                || def == typeof(ValueTuple<,,,>)
                || def == typeof(ValueTuple<,,,,>)
                || def == typeof(ValueTuple<,,,,,>)
                || def == typeof(ValueTuple<,,,,,,>)
                || def == typeof(ValueTuple<,,,,,,,>);
        }

        private static Expression LowerTupleEquality(Expression left, Expression right, bool equal)
        {
            if (left.Type != right.Type)
            {
                // Arity / element-type mismatch — let BCL surface the real error.
                return equal ? Expression.Equal(left, right) : Expression.NotEqual(left, right);
            }
            var fields = left.Type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            Expression test = null;
            foreach (var f in fields)
            {
                Expression a = Expression.Field(left, f);
                Expression b = Expression.Field(right, f);
                Expression cmp;
                if (IsValueTuple(f.FieldType))
                {
                    cmp = LowerTupleEquality(a, b, equal: true);
                }
                else
                {
                    NormalizeNullableOperands(ref a, ref b);
                    cmp = Expression.Equal(a, b);
                }
                test = test == null ? cmp : Expression.AndAlso(test, cmp);
            }
            if (test == null) test = Expression.Constant(true);
            return equal ? test : (Expression)Expression.Not(test);
        }

        private Type ResolveTypeFromSyntax(ExpressionSyntax typeNode)
        {
            var sym = m_Model.GetTypeInfo(typeNode).Type;
            if (sym == null)
                throw new InvalidOperationException($"Cannot resolve type from '{typeNode}'");
            return ResolveTypeSymbol(sym);
        }

        private Expression VisitLambda(SimpleLambdaExpressionSyntax lam, Type targetType)
        {
            if (targetType == null || !typeof(Delegate).IsAssignableFrom(targetType))
                throw new InvalidOperationException("Lambda needs delegate target type");
            var invoke = targetType.GetMethod("Invoke");
            var ps = invoke.GetParameters();
            if (ps.Length != 1) throw new NotSupportedException("Lite mode supports only single-param SimpleLambda; use (a,b)=>... for multi-param");
            var p = Expression.Parameter(ps[0].ParameterType, lam.Parameter.Identifier.ValueText);
            m_LambdaScopes.Push(new Dictionary<string, ParameterExpression>(StringComparer.Ordinal) { [p.Name] = p });
            try
            {
                var body = BuildLambdaBody(lam.Body, invoke.ReturnType);
                return Expression.Lambda(targetType, body, p);
            }
            finally { m_LambdaScopes.Pop(); }
        }

        private Expression VisitInvocation(InvocationExpressionSyntax inv)
        {
            // Fail-fast (BYREF): ref/out argument that names a session slot.
            // Detected here (before any method resolution) so the user gets a
            // clean ByRef-on-session message instead of a method-not-found error.
            if (inv.ArgumentList != null)
            {
                foreach (var a in inv.ArgumentList.Arguments)
                {
                    if (!a.RefKindKeyword.IsKind(SyntaxKind.None) && IsSessionSlotRoot(a.Expression))
                    {
                        var refKw = a.RefKindKeyword.ValueText;
                        throw new LiteCompilerException(
                            "E_SESSION_BYREF_FORBIDDEN",
                            $"Cannot pass session-scoped variable as '{refKw}' argument; Lite mode keeps session values as boxed slots. " +
                            $"Copy to a local first: 'var local = name; Method({refKw} local); name = local;'");
                    }
                }
            }

            // nameof(...) — Roslyn surfaces it as a constant string.
            var constVal = m_Model.GetConstantValue(inv);
            if (constVal.HasValue && constVal.Value is string nameofStr
                && inv.Expression is IdentifierNameSyntax nofId && nofId.Identifier.ValueText == "nameof")
            {
                return Expression.Constant(nameofStr, typeof(string));
            }

            if (inv.Expression is IdentifierNameSyntax target)
            {
                var name = target.Identifier.ValueText;
                Expression callee = null;
                Type calleeType = null;

                foreach (var scope in m_LambdaScopes)
                    if (scope.TryGetValue(name, out var p)) { callee = p; calleeType = p.Type; break; }
                if (callee == null)
                {
                    var t = LookupSlotType(name);
                    if (t != null)
                    {
                        callee = Expression.Convert(
                            Expression.Call(m_SlotsExpr, s_DictGet, Expression.Constant(name)), t);
                        calleeType = t;
                    }
                }
                if (callee == null)
                    throw new InvalidOperationException($"Cannot invoke '{name}': not in scope or slots");
                if (!typeof(Delegate).IsAssignableFrom(calleeType))
                    throw new NotSupportedException("Lite mode only invokes delegate-typed values");

                var invoke = calleeType.GetMethod("Invoke");
                var invokeParams = invoke.GetParameters();
                var args = inv.ArgumentList.Arguments
                    .Select((a, i) => VisitExpression(a.Expression, invokeParams[i].ParameterType))
                    .ToArray();
                return Expression.Invoke(callee, args);
            }
            if (inv.Expression is MemberAccessExpressionSyntax mae)
            {
                // Deadlock fail-fast: `.Wait()`, `.GetAwaiter()`, `.GetResult()`
                var methodName = mae.Name.Identifier.ValueText;
                if (methodName == "Wait" || methodName == "GetAwaiter")
                {
                    var containerType = m_Model.GetTypeInfo(mae.Expression).Type;
                    if (IsTaskLike(containerType))
                        RejectDeadlock("." + methodName + "()",
                            methodName == "Wait"
                                ? ".Wait() blocks the calling thread."
                                : ".GetAwaiter() is the entry point into .GetResult() — same deadlock risk.");
                }
                else if (methodName == "GetResult")
                {
                    var containerType = m_Model.GetTypeInfo(mae.Expression).Type;
                    if (IsAwaiterLike(containerType))
                        RejectDeadlock(".GetResult()",
                            "Calling .GetResult() on a TaskAwaiter blocks until the Task completes.");
                }

                // Method call: Math.Max(a,b) (static) or "hi".ToUpper() (instance).
                var sym = m_Model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                if (sym == null)
                    throw new InvalidOperationException($"Cannot resolve method symbol for '{inv}'");

                // Extension method call (`arr.Where(...)`): reduce to the static
                // form by prepending receiver as first arg. ReducedFrom is the
                // open-generic static def; Construct() with the reduced symbol's
                // type arguments yields the constructed static IMethodSymbol.
                if (sym.IsExtensionMethod && sym.ReducedFrom != null)
                {
                    var origStatic = sym.ReducedFrom;
                    IMethodSymbol constructedStatic = sym.TypeArguments.Length > 0
                        ? origStatic.Construct(sym.TypeArguments.ToArray())
                        : origStatic;
                    var staticMethod = ResolveMethod(constructedStatic);
                    var sParams = staticMethod.GetParameters();
                    var bound = BindArguments(inv.ArgumentList.Arguments, sParams, skipFirstParam: true);
                    var sArgs = new List<Expression> { VisitExpression(mae.Expression, sParams[0].ParameterType) };
                    sArgs.AddRange(bound);
                    return Expression.Call(null, staticMethod, sArgs);
                }

                var method = ResolveMethod(sym);
                var parameters = method.GetParameters();
                var args = BindArguments(inv.ArgumentList.Arguments, parameters, skipFirstParam: false);
                Expression receiver = method.IsStatic
                    ? null
                    : VisitExpression(mae.Expression, targetType: null);
                return Expression.Call(receiver, method, args);
            }
            throw new NotSupportedException($"Invocation of {inv.Expression.Kind()} not supported");
        }

        private Expression VisitMemberAccess(MemberAccessExpressionSyntax mae)
        {
            // Deadlock fail-fast: `task.Result` on Task/ValueTask
            if (mae.Name.Identifier.ValueText == "Result")
            {
                var containerType = m_Model.GetTypeInfo(mae.Expression).Type;
                if (IsTaskLike(containerType))
                    RejectDeadlock(".Result",
                        "Reading .Result on a Task blocks the calling thread.");
            }
            var sym = m_Model.GetSymbolInfo(mae).Symbol;
            if (sym is IPropertySymbol prop)
            {
                var info = ResolveProperty(prop);
                Expression receiver = info.GetMethod.IsStatic
                    ? null
                    : VisitExpression(mae.Expression, targetType: null);
                return Expression.Property(receiver, info);
            }
            if (sym is IFieldSymbol field)
            {
                var info = ResolveField(field);
                Expression receiver = info.IsStatic
                    ? null
                    : VisitExpression(mae.Expression, targetType: null);
                return Expression.Field(receiver, info);
            }
            throw new NotSupportedException($"MemberAccess to {sym?.Kind} ('{mae}') not supported as expression");
        }

        private Expression VisitObjectCreation(ObjectCreationExpressionSyntax obj)
        {
            var ctorSym = m_Model.GetSymbolInfo(obj).Symbol as IMethodSymbol;
            if (ctorSym == null)
                throw new InvalidOperationException($"Cannot resolve constructor for '{obj}'");
            var ctor = ResolveConstructor(ctorSym);
            var paramTypes = ctor.GetParameters();
            Expression[] args = obj.ArgumentList == null
                ? Array.Empty<Expression>()
                : BindArguments(obj.ArgumentList.Arguments, paramTypes, skipFirstParam: false);
            var newExpr = Expression.New(ctor, args);
            if (obj.Initializer != null && obj.Initializer.IsKind(SyntaxKind.CollectionInitializerExpression))
            {
                // Lower `new List<int> { 1, 2, 3 }` to:
                //   { var t = new List<int>(); t.Add(1); t.Add(2); t.Add(3); t }
                // Each element may be a single expr or a {k,v} sequence (Dictionary).
                var receiverType = ResolveTypeSymbol(ctorSym.ContainingType);
                var temp = Expression.Parameter(receiverType, "__init");
                var stmts = new List<Expression> { Expression.Assign(temp, newExpr) };
                foreach (var elem in obj.Initializer.Expressions)
                {
                    // Resolve the Add method via SemanticModel on this initializer element.
                    var addInfo = m_Model.GetCollectionInitializerSymbolInfo(elem);
                    if (!(addInfo.Symbol is IMethodSymbol addSym))
                        throw new InvalidOperationException($"Cannot resolve Add for collection init element '{elem}'");
                    var addMethod = ResolveMethod(addSym);
                    var addPs = addMethod.GetParameters();
                    Expression[] addArgs;
                    if (elem is InitializerExpressionSyntax sub && sub.IsKind(SyntaxKind.ComplexElementInitializerExpression))
                    {
                        addArgs = sub.Expressions
                            .Select((e, i) => VisitExpression(e, addPs[i].ParameterType))
                            .ToArray();
                    }
                    else
                    {
                        addArgs = new[] { VisitExpression(elem, addPs[0].ParameterType) };
                    }
                    stmts.Add(Expression.Call(temp, addMethod, addArgs));
                }
                stmts.Add(temp);
                return Expression.Block(receiverType, new[] { temp }, stmts);
            }
            if (obj.Initializer != null && obj.Initializer.IsKind(SyntaxKind.ObjectInitializerExpression))
            {
                var bindings = new List<MemberBinding>();
                foreach (var e in obj.Initializer.Expressions)
                {
                    if (!(e is AssignmentExpressionSyntax aex) || !(aex.Left is IdentifierNameSyntax id))
                        throw new NotSupportedException("Lite mode object init only supports 'Name = Value'");
                    var memberSym = m_Model.GetSymbolInfo(aex.Left).Symbol;
                    if (memberSym is IPropertySymbol ps)
                    {
                        var info = ResolveProperty(ps);
                        var value = VisitExpression(aex.Right, info.PropertyType);
                        bindings.Add(Expression.Bind(info, value));
                    }
                    else if (memberSym is IFieldSymbol fs)
                    {
                        var info = ResolveField(fs);
                        var value = VisitExpression(aex.Right, info.FieldType);
                        bindings.Add(Expression.Bind(info, value));
                    }
                    else
                    {
                        throw new NotSupportedException($"Object init for {memberSym?.Kind} '{id.Identifier.ValueText}' not supported");
                    }
                }
                return Expression.MemberInit(newExpr, bindings);
            }
            return newExpr;
        }

        private Expression VisitCast(CastExpressionSyntax cast)
        {
            var typeSym = m_Model.GetTypeInfo(cast.Type).Type;
            if (typeSym == null)
                throw new InvalidOperationException($"Cannot resolve cast target type '{cast.Type}'");
            var target = ResolveTypeSymbol(typeSym);
            var inner = VisitExpression(cast.Expression, target);
            return Expression.Convert(inner, target);
        }

        private Expression VisitConditional(ConditionalExpressionSyntax cond, Type targetType)
        {
            var test = VisitExpression(cond.Condition, typeof(bool));
            var ifTrue = VisitExpression(cond.WhenTrue, targetType);
            var ifFalse = VisitExpression(cond.WhenFalse, targetType);
            // If branches differ in type, find the common type. For Lite mode, just
            // unify to the wider one if convertible; else fail with a clear msg.
            if (ifTrue.Type != ifFalse.Type)
            {
                if (ifTrue.Type.IsAssignableFrom(ifFalse.Type))
                    ifFalse = Expression.Convert(ifFalse, ifTrue.Type);
                else if (ifFalse.Type.IsAssignableFrom(ifTrue.Type))
                    ifTrue = Expression.Convert(ifTrue, ifFalse.Type);
                else
                    throw new NotSupportedException($"Ternary branches differ ({ifTrue.Type.Name} vs {ifFalse.Type.Name}); Lite mode does not infer a common type");
            }
            return Expression.Condition(test, ifTrue, ifFalse);
        }

        private Expression VisitPrefixUnary(PrefixUnaryExpressionSyntax pre)
        {
            if (pre.IsKind(SyntaxKind.PreIncrementExpression) || pre.IsKind(SyntaxKind.PreDecrementExpression))
            {
                if (!(pre.Operand is IdentifierNameSyntax id))
                    throw new NotSupportedException($"Pre {pre.Kind()} on non-identifier not supported in Lite mode");
                var read = VisitIdentifier(id);
                int delta = pre.IsKind(SyntaxKind.PreIncrementExpression) ? 1 : -1;
                var newVal = Expression.Add(read, Expression.Convert(Expression.Constant(delta), read.Type));
                return WriteIdentifier(id.Identifier.ValueText, newVal);
            }

            // `^expr` builds a System.Index using fromEnd=true.
            if (pre.IsKind(SyntaxKind.IndexExpression))
            {
                var valueExpr = VisitExpression(pre.Operand, typeof(int));
                var indexCtor = typeof(Index).GetConstructor(new[] { typeof(int), typeof(bool) });
                return Expression.New(indexCtor, valueExpr, Expression.Constant(true));
            }

            var operand = VisitExpression(pre.Operand, null);
            switch (pre.Kind())
            {
                case SyntaxKind.UnaryMinusExpression: return Expression.Negate(operand);
                case SyntaxKind.UnaryPlusExpression: return Expression.UnaryPlus(operand);
                case SyntaxKind.LogicalNotExpression: return Expression.Not(operand);
                case SyntaxKind.BitwiseNotExpression: return Expression.OnesComplement(operand);
                default:
                    throw new NotSupportedException($"PrefixUnary {pre.Kind()} not supported");
            }
        }

        // `a..b`, `..b`, `a..`, `..` build a System.Range.
        private Expression VisitRange(RangeExpressionSyntax range)
        {
            Expression start = range.LeftOperand != null
                ? CoerceToIndex(VisitExpression(range.LeftOperand, null))
                : Expression.Property(null, typeof(Index).GetProperty("Start"));
            Expression end = range.RightOperand != null
                ? CoerceToIndex(VisitExpression(range.RightOperand, null))
                : Expression.Property(null, typeof(Index).GetProperty("End"));
            var rangeCtor = typeof(Range).GetConstructor(new[] { typeof(Index), typeof(Index) });
            return Expression.New(rangeCtor, start, end);
        }

        // Int operand -> Index from start; Index operand -> as-is.
        private static Expression CoerceToIndex(Expression e)
        {
            if (e.Type == typeof(Index)) return e;
            if (e.Type == typeof(int))
            {
                var implicitOp = typeof(Index).GetMethod("op_Implicit", new[] { typeof(int) });
                if (implicitOp != null) return Expression.Call(implicitOp, e);
                var ctor = typeof(Index).GetConstructor(new[] { typeof(int), typeof(bool) });
                return Expression.New(ctor, e, Expression.Constant(false));
            }
            throw new NotSupportedException($"Cannot coerce {e.Type.Name} to Index");
        }

        private Expression VisitPostfixUnary(PostfixUnaryExpressionSyntax post)
        {
            if (!(post.Operand is IdentifierNameSyntax id))
                throw new NotSupportedException($"Post {post.Kind()} on non-identifier not supported in Lite mode");
            int delta = post.Kind() switch
            {
                SyntaxKind.PostIncrementExpression => 1,
                SyntaxKind.PostDecrementExpression => -1,
                _ => throw new NotSupportedException($"PostfixUnary {post.Kind()} not supported"),
            };
            var read = VisitIdentifier(id);
            var tmp = Expression.Parameter(read.Type, "__post");
            var newVal = Expression.Add(tmp, Expression.Convert(Expression.Constant(delta), read.Type));
            var write = WriteIdentifier(id.Identifier.ValueText, newVal);
            // post-fix returns old value
            return Expression.Block(
                read.Type,
                new[] { tmp },
                Expression.Assign(tmp, read),
                write,
                tmp);
        }

        private Expression VisitElementAccess(ElementAccessExpressionSyntax elem)
        {
            var receiver = VisitExpression(elem.Expression, null);
            if (receiver.Type.IsArray)
            {
                // Single-arg array element access can be int, Index, or Range.
                // We peek at the first arg's resolved type to decide path.
                if (elem.ArgumentList.Arguments.Count == 1)
                {
                    var argRaw = VisitExpression(elem.ArgumentList.Arguments[0].Expression, null);
                    if (argRaw.Type == typeof(Index))
                    {
                        // arr[^1] => arr[arr.Length - value]  (when fromEnd) else arr[value]
                        var idxVar = Expression.Parameter(typeof(Index), "__ix");
                        var idxValue = Expression.Property(idxVar, "Value");
                        var fromEnd = Expression.Property(idxVar, "IsFromEnd");
                        var arrLen = Expression.ArrayLength(receiver);
                        var resolved = Expression.Condition(
                            fromEnd,
                            Expression.Subtract(arrLen, idxValue),
                            idxValue);
                        return Expression.Block(
                            receiver.Type.GetElementType(),
                            new[] { idxVar },
                            Expression.Assign(idxVar, argRaw),
                            Expression.ArrayIndex(receiver, resolved));
                    }
                    if (argRaw.Type == typeof(Range))
                    {
                        // arr[1..3] -> RuntimeHelpers.GetSubArray<T>(arr, range)
                        var elemTy = receiver.Type.GetElementType();
                        var getSubArray = typeof(System.Runtime.CompilerServices.RuntimeHelpers)
                            .GetMethod("GetSubArray")
                            ?.MakeGenericMethod(elemTy);
                        if (getSubArray == null)
                            throw new NotSupportedException("Range indexer on array: GetSubArray helper not available");
                        return Expression.Call(getSubArray, receiver, argRaw);
                    }
                    // int path (the common case)
                    if (argRaw.Type != typeof(int)) argRaw = Expression.Convert(argRaw, typeof(int));
                    return Expression.ArrayIndex(receiver, argRaw);
                }
                var idxArgs = elem.ArgumentList.Arguments
                    .Select(a => VisitExpression(a.Expression, typeof(int)))
                    .ToArray();
                // Multi-dim array: ArrayIndex with multiple args requires a
                // jagged-shape signature on some runtimes; use Array.GetValue
                // which is uniform on rank>1 arrays and IL2CPP-safe.
                var getValue = typeof(Array).GetMethod("GetValue",
                    Enumerable.Repeat(typeof(int), idxArgs.Length).ToArray());
                var boxed = Expression.Call(receiver, getValue, idxArgs);
                var elemType = receiver.Type.GetElementType();
                return Expression.Convert(boxed, elemType);
            }
            // Non-array: resolve indexer property via SemanticModel.
            var sym = m_Model.GetSymbolInfo(elem).Symbol as IPropertySymbol;
            if (sym == null || !sym.IsIndexer)
                throw new NotSupportedException($"Element access on {receiver.Type.Name}: no indexer");
            var prop = ResolveIndexer(sym, receiver.Type);
            var indexerParams = prop.GetIndexParameters();
            var args = elem.ArgumentList.Arguments
                .Select((a, i) => VisitExpression(a.Expression, indexerParams[i].ParameterType))
                .ToArray();
            return Expression.Property(receiver, prop, args);
        }

        private static PropertyInfo ResolveIndexer(IPropertySymbol p, Type containingType)
        {
            var paramTypes = p.Parameters.Select(x => ResolveTypeSymbol(x.Type)).ToArray();
            var returnType = ResolveTypeSymbol(p.Type);
            var bf = BindingFlags.Public | BindingFlags.NonPublic |
                     (p.IsStatic ? BindingFlags.Static : BindingFlags.Instance);
            var info = containingType.GetProperty("Item", bf, binder: null, returnType: returnType, types: paramTypes, modifiers: null);
            if (info != null) return info;
            throw new InvalidOperationException($"Cannot resolve indexer on {containingType.FullName} with arg types ({string.Join(",", paramTypes.Select(t => t.Name))})");
        }

        private Expression VisitArrayCreation(ArrayCreationExpressionSyntax arr)
        {
            var arrTypeSym = m_Model.GetTypeInfo(arr).Type as IArrayTypeSymbol;
            if (arrTypeSym == null)
                throw new InvalidOperationException($"Cannot resolve array type for '{arr}'");
            var elemType = ResolveTypeSymbol(arrTypeSym.ElementType);
            if (arr.Initializer == null)
            {
                // `new int[5]` / `new int[m, n]` — size expressions only.
                var rankSpec = arr.Type.RankSpecifiers[0];
                var sizes = rankSpec.Sizes
                    .Where(e => !(e is OmittedArraySizeExpressionSyntax))
                    .Select(e => VisitExpression(e, typeof(int)))
                    .ToArray();
                if (sizes.Length == 0)
                    throw new NotSupportedException("Array creation needs sizes or initializer");
                return Expression.NewArrayBounds(elemType, sizes);
            }
            int rank = arrTypeSym.Rank;
            if (rank == 1)
            {
                var elems = arr.Initializer.Expressions.Select(e => VisitExpression(e, elemType)).ToArray();
                return Expression.NewArrayInit(elemType, elems);
            }
            // Multi-dim: walk the nested initializer to figure out lengths,
            // then emit Array.CreateInstance(elemType, lengths) + a sequence
            // of SetValue(value, idx0, idx1, ...) calls.
            var lengths = new List<int>();
            InitializerExpressionSyntax cur = arr.Initializer;
            for (int d = 0; d < rank; d++)
            {
                lengths.Add(cur.Expressions.Count);
                if (d < rank - 1)
                {
                    if (!(cur.Expressions[0] is InitializerExpressionSyntax next))
                        throw new InvalidOperationException("Multi-dim array initializer shape malformed");
                    cur = next;
                }
            }
            var temp = Expression.Parameter(arrTypeSym.Rank > 1 ? elemType.MakeArrayType(rank) : elemType.MakeArrayType(), "__arr");
            var createCall = Expression.Call(
                typeof(Array).GetMethod("CreateInstance", new[] { typeof(Type) }.Concat(Enumerable.Repeat(typeof(int), rank)).ToArray()),
                new[] { (Expression)Expression.Constant(elemType, typeof(Type)) }
                    .Concat(lengths.Select(l => (Expression)Expression.Constant(l, typeof(int)))).ToArray());
            var stmts = new List<Expression> { Expression.Assign(temp, Expression.Convert(createCall, temp.Type)) };

            var setValue = typeof(Array).GetMethod("SetValue", new[] { typeof(object) }
                .Concat(Enumerable.Repeat(typeof(int), rank)).ToArray());

            void Recurse(InitializerExpressionSyntax init, int[] indices, int depth)
            {
                if (depth == rank - 1)
                {
                    for (int i = 0; i < init.Expressions.Count; i++)
                    {
                        indices[depth] = i;
                        var val = VisitExpression(init.Expressions[i], elemType);
                        if (val.Type != elemType) val = Expression.Convert(val, elemType);
                        var boxed = elemType.IsValueType ? Expression.Convert(val, typeof(object)) : (Expression)val;
                        var idxConsts = indices.Select(x => (Expression)Expression.Constant(x, typeof(int))).ToArray();
                        stmts.Add(Expression.Call(temp, setValue, new[] { (Expression)boxed }.Concat(idxConsts).ToArray()));
                    }
                }
                else
                {
                    for (int i = 0; i < init.Expressions.Count; i++)
                    {
                        indices[depth] = i;
                        Recurse((InitializerExpressionSyntax)init.Expressions[i], indices, depth + 1);
                    }
                }
            }
            Recurse(arr.Initializer, new int[rank], 0);
            stmts.Add(temp);
            return Expression.Block(temp.Type, new[] { temp }, stmts);
        }

        private static readonly MethodInfo s_StringFormat =
            typeof(string).GetMethod("Format", new[] { typeof(string), typeof(object[]) });

        private Expression VisitLambdaParen(ParenthesizedLambdaExpressionSyntax lam, Type targetType)
        {
            if (targetType == null || !typeof(Delegate).IsAssignableFrom(targetType))
                throw new InvalidOperationException("Lambda needs delegate target type");
            var invoke = targetType.GetMethod("Invoke");
            var invokeParams = invoke.GetParameters();
            if (lam.ParameterList.Parameters.Count != invokeParams.Length)
                throw new InvalidOperationException($"Lambda parameter count ({lam.ParameterList.Parameters.Count}) mismatches delegate {targetType.Name} ({invokeParams.Length})");
            var paramExprs = lam.ParameterList.Parameters.Select((p, i) =>
                Expression.Parameter(invokeParams[i].ParameterType, p.Identifier.ValueText)).ToArray();
            var scope = new Dictionary<string, ParameterExpression>(StringComparer.Ordinal);
            foreach (var p in paramExprs) scope[p.Name] = p;
            m_LambdaScopes.Push(scope);
            try
            {
                var body = BuildLambdaBody(lam.Body, invoke.ReturnType);
                return Expression.Lambda(targetType, body, paramExprs);
            }
            finally { m_LambdaScopes.Pop(); }
        }

        // x?.Member lowers to: var t = x; t == null ? default : t.Member.
        // For value-type receivers (always non-null at runtime), still emit the
        // null check — Lite mode doesn't optimize this away.
        // (a, b) = (1, 2)  /  (int a, int b) = (1, 2)  /  (int a, (int b, int c)) = ...
        private Expression VisitTupleDeconstruct(TupleExpressionSyntax lhs, ExpressionSyntax rhs)
        {
            var rhsExpr = VisitExpression(rhs, null);
            return TupleLhsInto(lhs, rhsExpr);
        }

        private Expression TupleLhsInto(TupleExpressionSyntax lhs, Expression rhsExpr)
        {
            var tmp = Expression.Parameter(rhsExpr.Type, "__deconstr");
            var stmts = new List<Expression> { Expression.Assign(tmp, rhsExpr) };

            for (int i = 0; i < lhs.Arguments.Count; i++)
            {
                var argExpr = lhs.Arguments[i].Expression;
                var itemField = tmp.Type.GetField("Item" + (i + 1));
                if (itemField == null)
                    throw new InvalidOperationException($"Tuple {tmp.Type.Name} has no Item{i + 1}");
                Expression itemRead = Expression.Field(tmp, itemField);
                Type itemType = itemField.FieldType;

                if (argExpr is DeclarationExpressionSyntax decl
                    && decl.Designation is SingleVariableDesignationSyntax svd)
                {
                    Type declType = decl.Type.IsVar
                        ? itemType
                        : ResolveTypeSymbol(m_Model.GetTypeInfo(decl.Type).Type);
                    var name = svd.Identifier.ValueText;
                    RegisterPendingSlot(name, declType);
                    stmts.Add(WriteIdentifier(name, itemRead));
                }
                else if (argExpr is DeclarationExpressionSyntax declP
                    && declP.Designation is ParenthesizedVariableDesignationSyntax nestedDesig)
                {
                    // `(int a, var (b, c)) = ...` — nested parenthesized inside DeclarationExpression
                    stmts.Add(VarDesignationInto(nestedDesig, itemRead));
                }
                else if (argExpr is IdentifierNameSyntax id)
                {
                    stmts.Add(WriteIdentifier(id.Identifier.ValueText, itemRead));
                }
                else if (argExpr is TupleExpressionSyntax nestedLhs)
                {
                    // `(int a, (int b, int c)) = ...` — nested TupleExpression
                    stmts.Add(TupleLhsInto(nestedLhs, itemRead));
                }
                else
                {
                    throw new NotSupportedException($"Tuple LHS element {argExpr.Kind()} not supported");
                }
            }
            stmts.Add(tmp);
            return Expression.Block(tmp.Type, new[] { tmp }, stmts);
        }

        // `var (a, b) = (1, 2)` — DeclarationExpression with ParenthesizedDesignation,
        // including nested forms like `var (a, (b, c)) = ...`.
        private Expression VisitVarTupleDeconstruct(TypeSyntax typeSyntax, ParenthesizedVariableDesignationSyntax pvd, ExpressionSyntax rhs)
        {
            var rhsExpr = VisitExpression(rhs, null);
            return VarDesignationInto(pvd, rhsExpr);
        }

        private Expression VarDesignationInto(ParenthesizedVariableDesignationSyntax pvd, Expression rhsExpr)
        {
            var tmp = Expression.Parameter(rhsExpr.Type, "__deconstr");
            var stmts = new List<Expression> { Expression.Assign(tmp, rhsExpr) };

            for (int i = 0; i < pvd.Variables.Count; i++)
            {
                var v = pvd.Variables[i];
                var itemField = tmp.Type.GetField("Item" + (i + 1));
                if (itemField == null)
                    throw new InvalidOperationException($"Tuple {tmp.Type.Name} has no Item{i + 1}");
                Expression itemRead = Expression.Field(tmp, itemField);
                Type itemType = itemField.FieldType;

                if (v is SingleVariableDesignationSyntax svd)
                {
                    var name = svd.Identifier.ValueText;
                    RegisterPendingSlot(name, itemType);
                    stmts.Add(WriteIdentifier(name, itemRead));
                }
                else if (v is ParenthesizedVariableDesignationSyntax nested)
                {
                    stmts.Add(VarDesignationInto(nested, itemRead));
                }
                else
                {
                    throw new NotSupportedException($"Variable designation {v.Kind()} not supported");
                }
            }
            stmts.Add(tmp);
            return Expression.Block(tmp.Type, new[] { tmp }, stmts);
        }

        // is-pattern expression (`o is string s` / `x is > 3`).
        // Declaration patterns register a slot so the variable is visible to
        // subsequent code (Lite mode simplification — strict C# scoping deferred).
        private Expression VisitIsPattern(IsPatternExpressionSyntax isp)
        {
            var operand = VisitExpression(isp.Expression, null);
            var tmp = Expression.Parameter(operand.Type, "__isp");
            var test = BuildPatternTest(tmp, isp.Pattern, out var sideEffect);
            var stmts = new List<Expression> { Expression.Assign(tmp, operand) };
            if (sideEffect != null) stmts.Add(sideEffect);
            stmts.Add(test);
            return Expression.Block(typeof(bool), new[] { tmp }, stmts);
        }

        // Builds a bool test for `operand matches pattern`. Returns sideEffect
        // if the pattern introduces a slot assignment that should run before
        // the test in the same block.
        private Expression BuildPatternTest(Expression operand, PatternSyntax pattern, out Expression sideEffect)
        {
            sideEffect = null;
            switch (pattern)
            {
                case ConstantPatternSyntax cp:
                {
                    var rhs = VisitExpression(cp.Expression, operand.Type);
                    // Same-type fast path (covers numeric-numeric, string-string,
                    // and ref-vs-null where BCL Equal works directly).
                    if (operand.Type == rhs.Type)
                        return Expression.Equal(operand, rhs);
                    // null literal: BCL accepts Equal(reference, null) regardless
                    // of the null's reported type — keep direct path.
                    if (cp.Expression.IsKind(SyntaxKind.NullLiteralExpression))
                        return Expression.Equal(operand, rhs);
                    // Mixed types (e.g. operand is object, rhs is int literal).
                    // BCL's Expression.Equal factory rejects this. Use static
                    // object.Equals which handles boxing + value equality.
                    var objEquals = typeof(object).GetMethod(
                        nameof(object.Equals),
                        BindingFlags.Static | BindingFlags.Public,
                        null, new[] { typeof(object), typeof(object) }, null);
                    var lhsArg = operand.Type.IsValueType
                        ? (Expression)Expression.Convert(operand, typeof(object))
                        : operand;
                    var rhsArg = rhs.Type.IsValueType
                        ? (Expression)Expression.Convert(rhs, typeof(object))
                        : rhs;
                    return Expression.Call(objEquals, lhsArg, rhsArg);
                }
                case DiscardPatternSyntax:
                    return Expression.Constant(true);
                case TypePatternSyntax tp:
                    return Expression.TypeIs(operand, ResolveTypeFromSyntax(tp.Type));
                case DeclarationPatternSyntax dp:
                {
                    var t = ResolveTypeFromSyntax(dp.Type);
                    if (!(dp.Designation is SingleVariableDesignationSyntax svd))
                        throw new NotSupportedException("Declaration pattern needs single var name");
                    var name = svd.Identifier.ValueText;
                    RegisterPendingSlot(name, t);
                    Expression boxedVal;
                    if (t.IsValueType)
                    {
                        // Value-type: only write when operand IS T, otherwise leave default.
                        boxedVal = Expression.Convert(
                            Expression.Condition(
                                Expression.TypeIs(operand, t),
                                Expression.Convert(operand, t),
                                Expression.Default(t)),
                            typeof(object));
                    }
                    else
                    {
                        boxedVal = Expression.TypeAs(operand, t);
                    }
                    sideEffect = Expression.Call(m_SlotsExpr, s_DictSet, Expression.Constant(name), boxedVal);
                    return Expression.TypeIs(operand, t);
                }
                case RelationalPatternSyntax rp:
                {
                    var rhs = VisitExpression(rp.Expression, operand.Type);
                    return rp.OperatorToken.Kind() switch
                    {
                        SyntaxKind.GreaterThanToken => Expression.GreaterThan(operand, rhs),
                        SyntaxKind.GreaterThanEqualsToken => Expression.GreaterThanOrEqual(operand, rhs),
                        SyntaxKind.LessThanToken => Expression.LessThan(operand, rhs),
                        SyntaxKind.LessThanEqualsToken => Expression.LessThanOrEqual(operand, rhs),
                        _ => throw new NotSupportedException($"Relational pattern op {rp.OperatorToken.Kind()}"),
                    };
                }
                case BinaryPatternSyntax bp:
                {
                    var leftTest = BuildPatternTest(operand, bp.Left, out var leftSide);
                    var rightTest = BuildPatternTest(operand, bp.Right, out var rightSide);
                    Expression combine = bp.Kind() == SyntaxKind.AndPattern
                        ? Expression.AndAlso(leftTest, rightTest)
                        : Expression.OrElse(leftTest, rightTest);
                    // Collapse side-effects into a sequential pre-step.
                    if (leftSide != null && rightSide != null)
                        sideEffect = Expression.Block(typeof(void), leftSide, rightSide);
                    else sideEffect = leftSide ?? rightSide;
                    return combine;
                }
                case ParenthesizedPatternSyntax pp:
                    return BuildPatternTest(operand, pp.Pattern, out sideEffect);
                case RecursivePatternSyntax rp:
                    return BuildRecursivePatternTest(operand, rp, out sideEffect);
                case ListPatternSyntax lp:
                    return BuildListPatternTest(operand, lp, out sideEffect);
                case UnaryPatternSyntax up when up.IsKind(SyntaxKind.NotPattern):
                    return Expression.Not(BuildPatternTest(operand, up.Pattern, out sideEffect));
                default:
                    throw new NotSupportedException($"Pattern {pattern.Kind()} not supported");
            }
        }

        // RecursivePattern: optional type + optional property clause + optional designation.
        // `obj is { X: 5, Y: > 3 }` → no type, property clause
        // `p is Point { X: 5 } pt` → type=Point, property clause, designation=pt
        private Expression BuildRecursivePatternTest(Expression operand, RecursivePatternSyntax rp, out Expression sideEffect)
        {
            Expression typeTest = null;
            Expression typedOperand = operand;
            if (rp.Type != null)
            {
                var t = ResolveTypeFromSyntax(rp.Type);
                typeTest = Expression.TypeIs(operand, t);
                typedOperand = Expression.Convert(operand, t);
            }

            var sideList = new List<Expression>();
            Expression combined = typeTest;

            if (rp.PropertyPatternClause != null)
            {
                foreach (var sub in rp.PropertyPatternClause.Subpatterns)
                {
                    string memberName;
                    if (sub.NameColon != null)
                        memberName = sub.NameColon.Name.Identifier.ValueText;
                    else if (sub.ExpressionColon != null && sub.ExpressionColon.Expression is IdentifierNameSyntax idExpr)
                        memberName = idExpr.Identifier.ValueText;
                    else
                        throw new NotSupportedException($"Property pattern subpattern shape {sub.Kind()} not supported");

                    var rcvType = typedOperand.Type;
                    var bfMember = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    var pi = rcvType.GetProperty(memberName, bfMember);
                    var fi = rcvType.GetField(memberName, bfMember);
                    Expression memberAccess;
                    if (pi != null) memberAccess = Expression.Property(typedOperand, pi);
                    else if (fi != null) memberAccess = Expression.Field(typedOperand, fi);
                    else throw new InvalidOperationException($"Member '{memberName}' not found on {rcvType.FullName}");

                    var subTest = BuildPatternTest(memberAccess, sub.Pattern, out var subSide);
                    if (subSide != null) sideList.Add(subSide);
                    combined = combined == null ? subTest : Expression.AndAlso(combined, subTest);
                }
            }

            if (combined == null) combined = Expression.Constant(true);

            if (rp.Designation is SingleVariableDesignationSyntax desig)
            {
                var name = desig.Identifier.ValueText;
                RegisterPendingSlot(name, typedOperand.Type);
                Expression boxed = typedOperand.Type.IsValueType
                    ? (Expression)Expression.Convert(typedOperand, typeof(object))
                    : typedOperand;
                sideList.Add(Expression.Call(m_SlotsExpr, s_DictSet, Expression.Constant(name), boxed));
            }

            if (sideList.Count > 0)
                sideEffect = sideList.Count == 1 ? sideList[0] : Expression.Block(typeof(void), sideList);
            else sideEffect = null;
            return combined;
        }

        // ListPattern `[1, 2, ..]` — Lite mode supports arrays only.
        // Slice `..` translates to a length-tolerance: head + tail constraints.
        private Expression BuildListPatternTest(Expression operand, ListPatternSyntax lp, out Expression sideEffect)
        {
            if (!operand.Type.IsArray)
                throw new NotSupportedException("List pattern: Lite mode supports arrays only");

            int sliceIdx = -1;
            for (int i = 0; i < lp.Patterns.Count; i++)
                if (lp.Patterns[i] is SlicePatternSyntax) { sliceIdx = i; break; }

            var sideList = new List<Expression>();
            Expression test;
            var lenExpr = Expression.ArrayLength(operand);

            if (sliceIdx < 0)
            {
                test = Expression.Equal(lenExpr, Expression.Constant(lp.Patterns.Count));
                for (int i = 0; i < lp.Patterns.Count; i++)
                {
                    var elemAccess = Expression.ArrayIndex(operand, Expression.Constant(i));
                    var elemTest = BuildPatternTest(elemAccess, lp.Patterns[i], out var elemSide);
                    if (elemSide != null) sideList.Add(elemSide);
                    test = Expression.AndAlso(test, elemTest);
                }
            }
            else
            {
                int headCount = sliceIdx;
                int tailCount = lp.Patterns.Count - sliceIdx - 1;
                test = Expression.GreaterThanOrEqual(lenExpr, Expression.Constant(headCount + tailCount));
                for (int i = 0; i < headCount; i++)
                {
                    var elemAccess = Expression.ArrayIndex(operand, Expression.Constant(i));
                    var elemTest = BuildPatternTest(elemAccess, lp.Patterns[i], out var elemSide);
                    if (elemSide != null) sideList.Add(elemSide);
                    test = Expression.AndAlso(test, elemTest);
                }
                for (int i = 0; i < tailCount; i++)
                {
                    var fromEnd = tailCount - i;
                    var elemAccess = Expression.ArrayIndex(operand,
                        Expression.Subtract(lenExpr, Expression.Constant(fromEnd)));
                    var elemTest = BuildPatternTest(elemAccess, lp.Patterns[sliceIdx + 1 + i], out var elemSide);
                    if (elemSide != null) sideList.Add(elemSide);
                    test = Expression.AndAlso(test, elemTest);
                }
            }

            if (sideList.Count > 0)
                sideEffect = sideList.Count == 1 ? sideList[0] : Expression.Block(typeof(void), sideList);
            else sideEffect = null;
            return test;
        }

        // LINQ query syntax: `from x in source [where ...] [select ...]`.
        // Lower to Enumerable extension calls (Where / Select / OrderBy ...).
        // Lite mode supports `from / where / select` only.
        private Expression VisitQueryExpression(QueryExpressionSyntax q)
        {
            Expression src = VisitExpression(q.FromClause.Expression, null);
            string itemVar = q.FromClause.Identifier.ValueText;
            return ApplyQueryBody(src, itemVar, q.Body);
        }

        // Process a QueryBodySyntax against the current source and iteration
        // variable. Supports: where + select + group by (as trailing) + join
        // (folded into trailing select) + into continuation (recursive).
        private Expression ApplyQueryBody(Expression src, string itemVar, QueryBodySyntax body)
        {
            // Detect single join — folded with trailing select. Multiple joins
            // or join + where/orderby etc. would need transparent identifiers,
            // out of v1 scope.
            JoinClauseSyntax joinClause = null;
            foreach (var c in body.Clauses)
            {
                if (c is JoinClauseSyntax jc)
                {
                    if (joinClause != null)
                        throw new NotSupportedException("Multiple join clauses in a single query body not supported");
                    joinClause = jc;
                }
            }

            if (joinClause != null)
            {
                foreach (var c in body.Clauses)
                {
                    if (c is JoinClauseSyntax) continue;
                    throw new NotSupportedException($"Join combined with {c.Kind()} not supported (use method syntax for complex joins)");
                }
                if (joinClause.Into != null)
                    throw new NotSupportedException("join...into (GroupJoin) not supported in Lite mode");
                if (!(body.SelectOrGroup is SelectClauseSyntax joinSel))
                    throw new NotSupportedException("Join must be followed by `select` (no `group by` after join)");
                src = EmitJoin(src, itemVar, joinClause, joinSel.Expression);
                if (body.Continuation != null)
                    src = ApplyQueryBody(src, body.Continuation.Identifier.ValueText, body.Continuation.Body);
                return src;
            }

            foreach (var c in body.Clauses)
            {
                if (c is WhereClauseSyntax wc)
                {
                    src = ApplyLinqLambda(src, itemVar, wc.Condition, "Where", returnsBool: true);
                }
                else
                {
                    throw new NotSupportedException($"Query clause {c.Kind()} not supported in Lite mode (only where + select + join + group)");
                }
            }

            if (body.SelectOrGroup is SelectClauseSyntax sc)
            {
                src = ApplyLinqLambda(src, itemVar, sc.Expression, "Select", returnsBool: false);
            }
            else if (body.SelectOrGroup is GroupClauseSyntax gc)
            {
                src = EmitGroupBy(src, itemVar, gc);
            }
            else
            {
                throw new NotSupportedException($"Query trailing clause {body.SelectOrGroup?.Kind()} not supported");
            }

            if (body.Continuation != null)
                src = ApplyQueryBody(src, body.Continuation.Identifier.ValueText, body.Continuation.Body);
            return src;
        }

        // Emits `outer.Join(inner, x => K1, y => K2, (x, y) => projection)`.
        // resultSelectorBody is the trailing select's expression with both
        // join-side identifiers in scope.
        private Expression EmitJoin(Expression outer, string outerVar, JoinClauseSyntax jc, ExpressionSyntax resultSelectorBody)
        {
            Expression inner = VisitExpression(jc.InExpression, null);
            string innerVar = jc.Identifier.ValueText;
            Type outerElem = GetEnumerableElementType(outer.Type);
            Type innerElem = GetEnumerableElementType(inner.Type);

            var outerParam = Expression.Parameter(outerElem, outerVar);
            m_LambdaScopes.Push(new Dictionary<string, ParameterExpression>(StringComparer.Ordinal) { [outerVar] = outerParam });
            Expression outerKeyBody;
            try { outerKeyBody = VisitExpression(jc.LeftExpression, null); }
            finally { m_LambdaScopes.Pop(); }

            var innerParam = Expression.Parameter(innerElem, innerVar);
            m_LambdaScopes.Push(new Dictionary<string, ParameterExpression>(StringComparer.Ordinal) { [innerVar] = innerParam });
            Expression innerKeyBody;
            try { innerKeyBody = VisitExpression(jc.RightExpression, null); }
            finally { m_LambdaScopes.Pop(); }

            Type keyType = outerKeyBody.Type;
            if (innerKeyBody.Type != keyType)
                innerKeyBody = Expression.Convert(innerKeyBody, keyType);

            var resultOuter = Expression.Parameter(outerElem, outerVar);
            var resultInner = Expression.Parameter(innerElem, innerVar);
            m_LambdaScopes.Push(new Dictionary<string, ParameterExpression>(StringComparer.Ordinal)
            {
                [outerVar] = resultOuter,
                [innerVar] = resultInner,
            });
            Expression resultBody;
            try { resultBody = VisitExpression(resultSelectorBody, null); }
            finally { m_LambdaScopes.Pop(); }
            Type resultType = resultBody.Type;

            var outerKeyLambda = Expression.Lambda(
                typeof(Func<,>).MakeGenericType(outerElem, keyType), outerKeyBody, outerParam);
            var innerKeyLambda = Expression.Lambda(
                typeof(Func<,>).MakeGenericType(innerElem, keyType), innerKeyBody, innerParam);
            var resultLambda = Expression.Lambda(
                typeof(Func<,,>).MakeGenericType(outerElem, innerElem, resultType),
                resultBody, resultOuter, resultInner);

            var joinOpen = typeof(System.Linq.Enumerable)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "Join" && m.IsGenericMethodDefinition && m.GetParameters().Length == 5);
            var joinClosed = joinOpen.MakeGenericMethod(outerElem, innerElem, keyType, resultType);
            return Expression.Call(joinClosed, outer, inner, outerKeyLambda, innerKeyLambda, resultLambda);
        }

        // Emits `src.GroupBy(x => key)` or `src.GroupBy(x => key, x => element)`
        // depending on whether the group expression is the identity (i.e.
        // `group x by k`).
        private Expression EmitGroupBy(Expression src, string itemVar, GroupClauseSyntax gc)
        {
            Type elemType = GetEnumerableElementType(src.Type);
            var param = Expression.Parameter(elemType, itemVar);
            m_LambdaScopes.Push(new Dictionary<string, ParameterExpression>(StringComparer.Ordinal) { [itemVar] = param });
            Expression elementBody;
            Expression keyBody;
            try
            {
                elementBody = VisitExpression(gc.GroupExpression, null);
                keyBody = VisitExpression(gc.ByExpression, null);
            }
            finally { m_LambdaScopes.Pop(); }

            bool elementIsIdentity = elementBody is ParameterExpression pe && pe == param;
            var keyLambda = Expression.Lambda(typeof(Func<,>).MakeGenericType(elemType, keyBody.Type), keyBody, param);

            MethodInfo open, closed;
            if (elementIsIdentity)
            {
                open = typeof(System.Linq.Enumerable)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .First(m => m.Name == "GroupBy"
                        && m.IsGenericMethodDefinition
                        && m.GetGenericArguments().Length == 2
                        && m.GetParameters().Length == 2);
                closed = open.MakeGenericMethod(elemType, keyBody.Type);
                return Expression.Call(closed, src, keyLambda);
            }
            else
            {
                // 3-arg GroupBy<TSource, TKey, TElement>(source, keySelector, elementSelector)
                var elementParam = Expression.Parameter(elemType, itemVar);
                m_LambdaScopes.Push(new Dictionary<string, ParameterExpression>(StringComparer.Ordinal) { [itemVar] = elementParam });
                Expression elementBody2;
                try { elementBody2 = VisitExpression(gc.GroupExpression, null); }
                finally { m_LambdaScopes.Pop(); }
                var elementLambda = Expression.Lambda(
                    typeof(Func<,>).MakeGenericType(elemType, elementBody2.Type), elementBody2, elementParam);
                open = typeof(System.Linq.Enumerable)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .First(m => m.Name == "GroupBy"
                        && m.IsGenericMethodDefinition
                        && m.GetGenericArguments().Length == 3
                        && m.GetParameters().Length == 3);
                closed = open.MakeGenericMethod(elemType, keyBody.Type, elementBody2.Type);
                return Expression.Call(closed, src, keyLambda, elementLambda);
            }
        }

        private static Type GetEnumerableElementType(Type collectionType)
        {
            if (collectionType.IsArray) return collectionType.GetElementType();
            var ie = collectionType.GetInterfaces()
                .Concat(new[] { collectionType })
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (ie == null)
                throw new InvalidOperationException($"Cannot determine element type for {collectionType.Name}");
            return ie.GetGenericArguments()[0];
        }

        private Expression ApplyLinqLambda(Expression src, string itemVar, ExpressionSyntax bodyExpr, string methodName, bool returnsBool)
        {
            // Determine element type from current src.Type (IEnumerable<T> or T[]).
            Type elemType = src.Type.IsArray
                ? src.Type.GetElementType()
                : src.Type.GetInterfaces()
                    .Concat(new[] { src.Type })
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    .Select(i => i.GetGenericArguments()[0])
                    .FirstOrDefault();
            if (elemType == null)
                throw new InvalidOperationException($"Cannot determine element type for query source {src.Type.Name}");

            var param = Expression.Parameter(elemType, itemVar);
            m_LambdaScopes.Push(new Dictionary<string, ParameterExpression>(StringComparer.Ordinal) { [itemVar] = param });
            Expression bodyExprE;
            Type resultElemType;
            try
            {
                bodyExprE = VisitExpression(bodyExpr, returnsBool ? typeof(bool) : null);
                if (returnsBool && bodyExprE.Type != typeof(bool))
                    bodyExprE = Expression.Convert(bodyExprE, typeof(bool));
                resultElemType = bodyExprE.Type;
            }
            finally { m_LambdaScopes.Pop(); }

            // Build typed delegate type and lambda.
            var delegType = returnsBool
                ? typeof(Func<,>).MakeGenericType(elemType, typeof(bool))
                : typeof(Func<,>).MakeGenericType(elemType, resultElemType);
            var lambda = Expression.Lambda(delegType, bodyExprE, param);

            // Find Enumerable.Where<T> / Enumerable.Select<T,R> overload.
            var enumerableType = typeof(System.Linq.Enumerable);
            var open = enumerableType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == methodName
                    && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 2
                    && m.GetParameters()[1].ParameterType.IsGenericType
                    && m.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(Func<,>));
            if (open == null)
                throw new InvalidOperationException($"Cannot find Enumerable.{methodName}<T> 2-arg overload");
            MethodInfo closed;
            if (methodName == "Where")
            {
                closed = open.MakeGenericMethod(elemType);
            }
            else
            {
                closed = open.MakeGenericMethod(elemType, resultElemType);
            }
            return Expression.Call(closed, src, lambda);
        }

        private Expression VisitTuple(TupleExpressionSyntax tup)
        {
            // Lower `(1, 2)` to `new ValueTuple<int,int>(1, 2)`. Roslyn already
            // gave a tuple type with the right ITypeSymbol arguments.
            var sym = m_Model.GetTypeInfo(tup).Type as INamedTypeSymbol;
            if (sym == null) throw new InvalidOperationException("Cannot resolve tuple type");
            var underlyingType = ResolveTypeSymbol(sym);
            var argTypes = sym.TupleElements.IsDefault
                ? sym.TypeArguments.Select(ResolveTypeSymbol).ToArray()
                : sym.TupleElements.Select(e => ResolveTypeSymbol(e.Type)).ToArray();
            var ctor = underlyingType.GetConstructor(argTypes);
            if (ctor == null)
                throw new InvalidOperationException($"No matching ValueTuple ctor on {underlyingType.FullName}");
            var values = tup.Arguments
                .Select((a, i) => VisitExpression(a.Expression, argTypes[i]))
                .ToArray();
            for (int i = 0; i < values.Length; i++)
                if (values[i].Type != argTypes[i]) values[i] = Expression.Convert(values[i], argTypes[i]);
            return Expression.New(ctor, values);
        }

        private Expression VisitConditionalAccess(ConditionalAccessExpressionSyntax cae)
        {
            var receiver = VisitExpression(cae.Expression, null);
            return BuildNullPropagation(receiver, tmp => TranslateMemberBinding(cae.WhenNotNull, tmp));
        }

        // Builds the standard `?.` lowering: evaluate `source` into a temp, then
        // null-check it; on null return the lifted null (Nullable<T> for value
        // types, plain null for refs); on non-null run `buildBody(tmp)`. Used by
        // both the outermost `?.` (VisitConditionalAccess) and the recursive
        // chained-`?.` branch in TranslateMemberBinding.
        private static Expression BuildNullPropagation(Expression source, Func<ParameterExpression, Expression> buildBody)
        {
            var tmp = Expression.Parameter(source.Type, "__qa");
            var notNullBody = buildBody(tmp);

            Type resultType = notNullBody.Type;
            if (resultType.IsValueType && Nullable.GetUnderlyingType(resultType) == null)
            {
                resultType = typeof(Nullable<>).MakeGenericType(resultType);
                notNullBody = Expression.Convert(notNullBody, resultType);
            }
            Expression nullLiteral = resultType.IsValueType
                ? (Expression)Expression.Default(resultType)
                : Expression.Constant(null, resultType);
            Expression nullCheck = source.Type.IsValueType
                ? (Expression)Expression.Constant(false)
                : Expression.Equal(tmp, Expression.Constant(null, source.Type));

            return Expression.Block(
                new[] { tmp },
                Expression.Assign(tmp, source),
                Expression.Condition(nullCheck, nullLiteral, notNullBody));
        }

        // Translates the right-hand chain of a `?.`. The chain root is a
        // MemberBindingExpressionSyntax (`.Member`); we rewrite it as a real
        // member access against the supplied placeholder receiver.
        private Expression TranslateMemberBinding(ExpressionSyntax node, Expression receiver)
        {
            if (node is MemberBindingExpressionSyntax mbe)
            {
                var sym = m_Model.GetSymbolInfo(mbe).Symbol;
                if (sym is IPropertySymbol ps)
                    return Expression.Property(ps.IsStatic ? null : receiver, ResolveProperty(ps));
                if (sym is IFieldSymbol fs)
                    return Expression.Field(fs.IsStatic ? null : receiver, ResolveField(fs));
                throw new NotSupportedException($"?. binding to {sym?.Kind} not supported");
            }
            if (node is InvocationExpressionSyntax inv && inv.Expression is MemberBindingExpressionSyntax mb2)
            {
                var sym = m_Model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                if (sym == null) throw new InvalidOperationException($"Cannot resolve ?. method '{inv}'");
                var method = ResolveMethod(sym);
                var parameters = method.GetParameters();
                var args = inv.ArgumentList.Arguments
                    .Select((a, i) => VisitExpression(a.Expression, parameters[i].ParameterType))
                    .ToArray();
                return Expression.Call(method.IsStatic ? null : receiver, method, args);
            }
            if (node is ConditionalAccessExpressionSyntax inner)
            {
                // Chained `?.`: Roslyn parses `s?.A?.B` as
                //   CAE(s, CAE(MemberBinding(A), MemberBinding(B)))
                // so inner.Expression binds to OUTER receiver, and inner.WhenNotNull
                // binds to the result of inner.Expression. Each link is its own
                // null-check stage, hence the recursive BuildNullPropagation call.
                var stage1 = TranslateMemberBinding(inner.Expression, receiver);
                return BuildNullPropagation(stage1, tmp => TranslateMemberBinding(inner.WhenNotNull, tmp));
            }
            throw new NotSupportedException($"?. continuation kind {node.Kind()} not supported");
        }

        // switch expr lowered to nested ternaries. Lite mode supports constant-pattern
        // and the discard pattern `_`. Type-patterns, when-clauses, etc. are out.
        private Expression VisitSwitchExpression(SwitchExpressionSyntax sw)
        {
            var subject = VisitExpression(sw.GoverningExpression, null);
            var tmp = Expression.Parameter(subject.Type, "__sw");

            Expression result = Expression.Throw(
                Expression.New(typeof(System.Runtime.CompilerServices.SwitchExpressionException)),
                typeof(object));
            // The result type is the unified type of all arm bodies. For Lite mode
            // simplicity, use the first arm's body type.
            Type resultType = null;
            var arms = new List<(Expression test, Expression sideEffect, Expression value)>();
            foreach (var arm in sw.Arms)
            {
                var test = BuildPatternTest(tmp, arm.Pattern, out var armSide);
                if (arm.WhenClause != null)
                {
                    // when clause runs *after* pattern matched — sequence the
                    // pattern side-effect (slot write for declaration patterns)
                    // before the when condition.
                    var whenExpr = VisitExpression(arm.WhenClause.Condition, typeof(bool));
                    if (armSide != null)
                    {
                        test = Expression.AndAlso(test, Expression.Block(typeof(bool), armSide, whenExpr));
                        armSide = null;
                    }
                    else
                    {
                        test = Expression.AndAlso(test, whenExpr);
                    }
                }
                var armValue = VisitExpression(arm.Expression, null);
                if (resultType == null) resultType = armValue.Type;
                arms.Add((test, armSide, armValue));
            }

            result = Expression.Throw(
                Expression.New(typeof(System.Runtime.CompilerServices.SwitchExpressionException)),
                resultType ?? typeof(object));
            for (int i = arms.Count - 1; i >= 0; i--)
            {
                var arm = arms[i];
                var armValue = arm.value;
                if (resultType != null && armValue.Type != resultType)
                {
                    if (resultType.IsAssignableFrom(armValue.Type))
                        armValue = Expression.Convert(armValue, resultType);
                }
                // Build an arm-test: side-effect (slot write for declaration
                // patterns) sequenced before the actual test expression.
                var armTest = arm.sideEffect == null
                    ? arm.test
                    : Expression.Block(typeof(bool), arm.sideEffect, arm.test);
                result = Expression.Condition(armTest, armValue, result);
            }

            return Expression.Block(
                resultType ?? typeof(object),
                new[] { tmp },
                Expression.Assign(tmp, subject),
                result);
        }

        // Shared body builder for expression-body and statement-body lambdas.
        // Statement bodies install a fresh ReturnLabel so `return expr;` lowers
        // to Expression.Return to that label.
        private Expression BuildLambdaBody(CSharpSyntaxNode body, Type returnType)
        {
            if (body is ExpressionSyntax bodyExpr)
            {
                var e = VisitExpression(bodyExpr, returnType);
                if (e.Type != returnType) e = Expression.Convert(e, returnType);
                return e;
            }
            if (body is BlockSyntax block)
            {
                var prevLabel = m_ReturnLabel;
                var prevType = m_ReturnType;
                var label = Expression.Label(returnType, "lambda_return");
                m_ReturnLabel = label;
                m_ReturnType = returnType;
                try
                {
                    var stmts = new List<Expression>();
                    foreach (var s in block.Statements) stmts.AddRange(VisitStatement(s));
                    // Trailing default in case no explicit return is hit
                    Expression defaultExpr = returnType == typeof(void)
                        ? (Expression)Expression.Empty()
                        : Expression.Default(returnType);
                    stmts.Add(Expression.Label(label, defaultExpr));
                    return Expression.Block(returnType, stmts);
                }
                finally
                {
                    m_ReturnLabel = prevLabel;
                    m_ReturnType = prevType;
                }
            }
            throw new NotSupportedException($"Lambda body kind {body.Kind()} not supported");
        }

        // foreach: array uses an index loop; IEnumerable / IEnumerable<T>
        // lowers to GetEnumerator + MoveNext + Current with try-finally Dispose.
        private Expression VisitForEach(ForEachStatementSyntax fe)
        {
            var collExpr = VisitExpression(fe.Expression, null);
            if (!collExpr.Type.IsArray)
                return VisitForEachEnumerable(fe, collExpr);

            var elemType = collExpr.Type.GetElementType();
            var coll = Expression.Parameter(collExpr.Type, "__coll");
            var idx = Expression.Parameter(typeof(int), "__i");
            var loopVar = Expression.Parameter(elemType, fe.Identifier.ValueText);
            var breakLabel = Expression.Label("foreach_exit");

            var continueLabel = Expression.Label("foreach_cont");
            var scope = new Dictionary<string, ParameterExpression>(StringComparer.Ordinal) { [fe.Identifier.ValueText] = loopVar };
            m_LambdaScopes.Push(scope);
            m_LoopLabels.Push((breakLabel, continueLabel));
            try
            {
                var body = StatementToExpression(fe.Statement);
                return Expression.Block(
                    new[] { coll, idx, loopVar },
                    Expression.Assign(coll, collExpr),
                    Expression.Assign(idx, Expression.Constant(0)),
                    Expression.Loop(
                        Expression.IfThenElse(
                            Expression.LessThan(idx, Expression.ArrayLength(coll)),
                            Expression.Block(typeof(void),
                                Expression.Assign(loopVar, Expression.ArrayIndex(coll, idx)),
                                ToVoid(body),
                                Expression.Label(continueLabel),
                                Expression.Assign(idx, Expression.Add(idx, Expression.Constant(1)))),
                            Expression.Break(breakLabel)),
                        breakLabel));
            }
            finally { m_LoopLabels.Pop(); m_LambdaScopes.Pop(); }
        }

        private readonly Dictionary<string, LabelTarget> m_UserLabels =
            new Dictionary<string, LabelTarget>(StringComparer.Ordinal);

        private LabelTarget GetOrCreateUserLabel(string name)
        {
            if (m_UserLabels.TryGetValue(name, out var lt)) return lt;
            lt = Expression.Label(name);
            m_UserLabels[name] = lt;
            return lt;
        }

        private Expression VisitLabeledStatement(LabeledStatementSyntax labelled)
        {
            var lt = GetOrCreateUserLabel(labelled.Identifier.ValueText);
            var body = StatementToExpression(labelled.Statement);
            return Expression.Block(typeof(void), Expression.Label(lt), ToVoid(body));
        }

        private Expression VisitGoto(GotoStatementSyntax gt)
        {
            if (!gt.IsKind(SyntaxKind.GotoStatement))
                throw new NotSupportedException($"goto {gt.Kind()} (case/default) not supported in Lite mode");
            if (!(gt.Expression is IdentifierNameSyntax id))
                throw new NotSupportedException("goto requires label identifier");
            return Expression.Goto(GetOrCreateUserLabel(id.Identifier.ValueText));
        }

        private Expression VisitLock(LockStatementSyntax ls)
        {
            // lock(obj) body  =>  Monitor.Enter(obj, ref taken); try { body } finally { if (taken) Monitor.Exit(obj); }
            // Lite mode simplified to Monitor.Enter / Exit without lockTaken.
            var lockExpr = VisitExpression(ls.Expression, null);
            var tmp = Expression.Parameter(typeof(object), "__lock");
            var enter = typeof(System.Threading.Monitor).GetMethod("Enter", new[] { typeof(object) });
            var exit = typeof(System.Threading.Monitor).GetMethod("Exit", new[] { typeof(object) });
            var body = StatementToExpression(ls.Statement);
            return Expression.Block(
                new[] { tmp },
                Expression.Assign(tmp, Expression.Convert(lockExpr, typeof(object))),
                Expression.Call(enter, tmp),
                Expression.TryFinally(ToVoid(body), Expression.Call(exit, tmp)));
        }

        private Expression VisitDoWhile(DoStatementSyntax ds)
        {
            var breakLabel = Expression.Label("dowhile_exit");
            var continueLabel = Expression.Label("dowhile_cont");
            m_LoopLabels.Push((breakLabel, continueLabel));
            try
            {
                var body = StatementToExpression(ds.Statement);
                var test = VisitExpression(ds.Condition, typeof(bool));
                return Expression.Loop(
                    Expression.Block(typeof(void),
                        ToVoid(body),
                        Expression.Label(continueLabel),
                        Expression.IfThen(
                            Expression.Not(test),
                            Expression.Break(breakLabel))),
                    breakLabel);
            }
            finally { m_LoopLabels.Pop(); }
        }

        // C# switch statement, with case / default labels.
        // Lower to a sequence of if-elseif based on BuildPatternTest.
        // case constant + when supported; fallthrough not supported (Lite mode).
        private Expression VisitSwitchStatement(SwitchStatementSyntax sw)
        {
            var subject = VisitExpression(sw.Expression, null);
            var tmp = Expression.Parameter(subject.Type, "__sw_stmt");
            var breakLabel = Expression.Label("switch_exit");
            // Push break label so `break;` inside cases works; no continue.
            m_LoopLabels.Push((breakLabel, breakLabel));
            try
            {
                Expression chain = Expression.Empty();
                SwitchSectionSyntax defaultSection = null;
                var sections = new List<(Expression test, Expression sideEffect, Expression body)>();
                foreach (var section in sw.Sections)
                {
                    bool isDefault = section.Labels.Any(l => l is DefaultSwitchLabelSyntax);
                    if (isDefault) { defaultSection = section; continue; }
                    Expression sectionTest = null;
                    Expression sectionSide = null;
                    foreach (var lbl in section.Labels)
                    {
                        Expression labelTest = null;
                        Expression labelSide = null;
                        if (lbl is CaseSwitchLabelSyntax cs)
                        {
                            var v = VisitExpression(cs.Value, tmp.Type);
                            labelTest = Expression.Equal(tmp, v);
                        }
                        else if (lbl is CasePatternSwitchLabelSyntax cps)
                        {
                            labelTest = BuildPatternTest(tmp, cps.Pattern, out labelSide);
                            if (cps.WhenClause != null)
                            {
                                var when = VisitExpression(cps.WhenClause.Condition, typeof(bool));
                                if (labelSide != null)
                                {
                                    labelTest = Expression.AndAlso(labelTest, Expression.Block(typeof(bool), labelSide, when));
                                    labelSide = null;
                                }
                                else labelTest = Expression.AndAlso(labelTest, when);
                            }
                        }
                        else continue;
                        sectionTest = sectionTest == null ? labelTest : Expression.OrElse(sectionTest, labelTest);
                        if (labelSide != null) sectionSide = sectionSide == null ? labelSide : Expression.Block(typeof(void), sectionSide, labelSide);
                    }
                    var bodyExprs = new List<Expression>();
                    foreach (var s in section.Statements)
                    {
                        // Skip a single trailing `break;` since we synthesize
                        // a break label after each section.
                        if (s is BreakStatementSyntax) continue;
                        bodyExprs.AddRange(VisitStatement(s));
                    }
                    var body = bodyExprs.Count == 0
                        ? (Expression)Expression.Empty()
                        : Expression.Block(typeof(void), bodyExprs.Select(ToVoid));
                    sections.Add((sectionTest ?? Expression.Constant(true), sectionSide, body));
                }

                // Default body
                Expression defaultBody = Expression.Empty();
                if (defaultSection != null)
                {
                    var bodyExprs = new List<Expression>();
                    foreach (var s in defaultSection.Statements)
                    {
                        if (s is BreakStatementSyntax) continue;
                        bodyExprs.AddRange(VisitStatement(s));
                    }
                    defaultBody = bodyExprs.Count == 0
                        ? (Expression)Expression.Empty()
                        : Expression.Block(typeof(void), bodyExprs.Select(ToVoid));
                }

                Expression nested = ToVoid(defaultBody);
                for (int i = sections.Count - 1; i >= 0; i--)
                {
                    var sec = sections[i];
                    var testExpr = sec.sideEffect == null
                        ? sec.test
                        : Expression.Block(typeof(bool), sec.sideEffect, sec.test);
                    nested = Expression.IfThenElse(testExpr, ToVoid(sec.body), nested);
                }

                return Expression.Block(
                    typeof(void),
                    new[] { tmp },
                    Expression.Assign(tmp, subject),
                    nested,
                    Expression.Label(breakLabel));
            }
            finally { m_LoopLabels.Pop(); }
        }

        private Expression VisitForEachEnumerable(ForEachStatementSyntax fe, Expression collExpr)
        {
            Type elemType;
            Type enumeratorType;
            MethodInfo getEnumerator;
            var genIEnumerable = collExpr.Type.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (collExpr.Type.IsGenericType && collExpr.Type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                genIEnumerable = collExpr.Type;
            if (genIEnumerable != null)
            {
                elemType = genIEnumerable.GetGenericArguments()[0];
                getEnumerator = genIEnumerable.GetMethod("GetEnumerator");
                enumeratorType = getEnumerator.ReturnType; // IEnumerator<T>
            }
            else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(collExpr.Type))
            {
                elemType = typeof(object);
                getEnumerator = typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator");
                enumeratorType = typeof(System.Collections.IEnumerator);
            }
            else
            {
                throw new NotSupportedException($"foreach over {collExpr.Type.Name}: no IEnumerable/IEnumerable<T> interface");
            }
            var moveNext = typeof(System.Collections.IEnumerator).GetMethod("MoveNext");
            var currentProp = enumeratorType.GetProperty("Current");

            var enumerator = Expression.Parameter(enumeratorType, "__enum");
            var loopVar = Expression.Parameter(elemType, fe.Identifier.ValueText);
            var breakLabel = Expression.Label("foreach_exit");
            var continueLabel = Expression.Label("foreach_cont");

            m_LambdaScopes.Push(new Dictionary<string, ParameterExpression>(StringComparer.Ordinal) { [fe.Identifier.ValueText] = loopVar });
            m_LoopLabels.Push((breakLabel, continueLabel));
            try
            {
                var body = StatementToExpression(fe.Statement);
                var currentRead = (Expression)Expression.Property(enumerator, currentProp);
                if (currentRead.Type != elemType) currentRead = Expression.Convert(currentRead, elemType);
                var loop = Expression.Loop(
                    Expression.IfThenElse(
                        Expression.Call(enumerator, moveNext),
                        Expression.Block(typeof(void),
                            Expression.Assign(loopVar, currentRead),
                            ToVoid(body),
                            Expression.Label(continueLabel)),
                        Expression.Break(breakLabel)),
                    breakLabel);

                // try-finally: if enumerator is IDisposable, Dispose.
                var disposable = Expression.Parameter(typeof(IDisposable), "__disp");
                var finallyBody = Expression.Block(
                    new[] { disposable },
                    Expression.Assign(disposable, Expression.TypeAs(enumerator, typeof(IDisposable))),
                    Expression.IfThen(
                        Expression.NotEqual(disposable, Expression.Constant(null, typeof(IDisposable))),
                        Expression.Call(disposable, typeof(IDisposable).GetMethod("Dispose"))));

                return Expression.Block(
                    new[] { enumerator, loopVar },
                    Expression.Assign(enumerator, Expression.Call(collExpr, getEnumerator)),
                    Expression.TryFinally(loop, finallyBody));
            }
            finally { m_LoopLabels.Pop(); m_LambdaScopes.Pop(); }
        }

        // for-loop lowered to { init; while (cond) { body; incr; } }. The
        // for-init declarations become true block-scoped locals, not slots.
        private Expression VisitFor(ForStatementSyntax fs)
        {
            var localVars = new List<ParameterExpression>();
            var initStmts = new List<Expression>();
            var scope = new Dictionary<string, ParameterExpression>(StringComparer.Ordinal);

            if (fs.Declaration != null)
            {
                var declType = fs.Declaration.Type.IsVar
                    ? null
                    : ResolveTypeSymbol(m_Model.GetTypeInfo(fs.Declaration.Type).Type);
                foreach (var v in fs.Declaration.Variables)
                {
                    Expression init = v.Initializer != null
                        ? VisitExpression(v.Initializer.Value, declType)
                        : null;
                    var t = declType ?? init?.Type
                        ?? throw new InvalidOperationException($"Cannot infer for-init type for '{v.Identifier.ValueText}'");
                    var p = Expression.Parameter(t, v.Identifier.ValueText);
                    localVars.Add(p);
                    scope[v.Identifier.ValueText] = p;
                    if (init != null) initStmts.Add(Expression.Assign(p, init));
                }
            }
            foreach (var initExpr in fs.Initializers)
            {
                initStmts.Add(ToVoid(VisitExpression(initExpr, null)));
            }

            var breakLabel = Expression.Label("for_exit");
            var continueLabel = Expression.Label("for_cont");

            m_LambdaScopes.Push(scope);
            m_LoopLabels.Push((breakLabel, continueLabel));
            try
            {
                var cond = fs.Condition != null
                    ? VisitExpression(fs.Condition, typeof(bool))
                    : (Expression)Expression.Constant(true);
                var body = StatementToExpression(fs.Statement);
                var incrs = fs.Incrementors.Select(i => ToVoid(VisitExpression(i, null))).ToList();

                var bodyAndIncr = new List<Expression> { ToVoid(body), Expression.Label(continueLabel) };
                bodyAndIncr.AddRange(incrs);

                var loop = Expression.Loop(
                    Expression.IfThenElse(
                        cond,
                        Expression.Block(typeof(void), bodyAndIncr),
                        Expression.Break(breakLabel)),
                    breakLabel);

                var all = new List<Expression>(initStmts) { loop };
                return Expression.Block(localVars, all);
            }
            finally { m_LoopLabels.Pop(); m_LambdaScopes.Pop(); }
        }

        // using (var x = ...) body  =>  block: assign x, try body, finally Dispose.
        private Expression VisitUsing(UsingStatementSyntax us)
        {
            var locals = new List<ParameterExpression>();
            var initStmts = new List<Expression>();
            var disposables = new List<ParameterExpression>();
            var scope = new Dictionary<string, ParameterExpression>(StringComparer.Ordinal);

            if (us.Declaration != null)
            {
                var declType = us.Declaration.Type.IsVar
                    ? null
                    : ResolveTypeSymbol(m_Model.GetTypeInfo(us.Declaration.Type).Type);
                foreach (var v in us.Declaration.Variables)
                {
                    if (v.Initializer == null)
                        throw new InvalidOperationException($"using local '{v.Identifier.ValueText}' needs initializer");
                    var init = VisitExpression(v.Initializer.Value, declType);
                    var t = declType ?? init.Type;
                    var p = Expression.Parameter(t, v.Identifier.ValueText);
                    locals.Add(p);
                    scope[v.Identifier.ValueText] = p;
                    initStmts.Add(Expression.Assign(p, init));
                    disposables.Add(p);
                }
            }
            else if (us.Expression != null)
            {
                var init = VisitExpression(us.Expression, null);
                var p = Expression.Parameter(init.Type, "__using");
                locals.Add(p);
                initStmts.Add(Expression.Assign(p, init));
                disposables.Add(p);
            }
            else
            {
                throw new InvalidOperationException("using needs declaration or expression");
            }

            m_LambdaScopes.Push(scope);
            try
            {
                var body = StatementToExpression(us.Statement);
                var finallyExprs = new List<Expression>();
                foreach (var d in disposables)
                {
                    var dispTmp = Expression.Parameter(typeof(IDisposable), "__d_" + d.Name);
                    finallyExprs.Add(Expression.Block(
                        new[] { dispTmp },
                        Expression.Assign(dispTmp, Expression.TypeAs(d, typeof(IDisposable))),
                        Expression.IfThen(
                            Expression.NotEqual(dispTmp, Expression.Constant(null, typeof(IDisposable))),
                            Expression.Call(dispTmp, typeof(IDisposable).GetMethod("Dispose")))));
                }
                Expression finallyBlock = finallyExprs.Count == 1
                    ? finallyExprs[0]
                    : (Expression)Expression.Block(typeof(void), finallyExprs);
                var tryFin = Expression.TryFinally(ToVoid(body), finallyBlock);
                var all = new List<Expression>(initStmts) { tryFin };
                return Expression.Block(locals, all);
            }
            finally { m_LambdaScopes.Pop(); }
        }

        private Expression VisitTry(TryStatementSyntax ts)
        {
            var body = StatementToExpression(ts.Block);
            var catches = new List<CatchBlock>();
            foreach (var c in ts.Catches)
            {
                if (c.Declaration == null)
                {
                    catches.Add(Expression.Catch(typeof(Exception), ToVoid(StatementToExpression(c.Block))));
                    continue;
                }
                var exTypeSym = m_Model.GetTypeInfo(c.Declaration.Type).Type;
                var exType = ResolveTypeSymbol(exTypeSym);
                var paramName = c.Declaration.Identifier.ValueText;
                if (string.IsNullOrEmpty(paramName))
                {
                    // `catch (FooEx)` without binding identifier
                    catches.Add(Expression.Catch(exType, ToVoid(StatementToExpression(c.Block))));
                    continue;
                }
                var p = Expression.Parameter(exType, paramName);
                m_LambdaScopes.Push(new Dictionary<string, ParameterExpression>(StringComparer.Ordinal) { [paramName] = p });
                try
                {
                    var handler = StatementToExpression(c.Block);
                    catches.Add(Expression.Catch(p, ToVoid(handler)));
                }
                finally { m_LambdaScopes.Pop(); }
            }
            if (ts.Finally != null)
            {
                var fin = StatementToExpression(ts.Finally.Block);
                return Expression.TryCatchFinally(ToVoid(body), ToVoid(fin), catches.ToArray());
            }
            return Expression.TryCatch(ToVoid(body), catches.ToArray());
        }

        private Expression VisitInterpolated(InterpolatedStringExpressionSyntax interp)
        {
            // Lower to string.Format(format, params object[]).
            // Format string preserves alignment ({N,A}) and format spec ({N:F2})
            // clauses; literal { and } in text are re-escaped to {{ }}.
            var format = new System.Text.StringBuilder();
            var args = new List<Expression>();
            foreach (var c in interp.Contents)
            {
                if (c is InterpolatedStringTextSyntax text)
                {
                    // For InterpolatedStringTextToken, ValueText already preserves
                    // {{ and }} (they're part of format-string syntax, not C# escapes).
                    // Appending verbatim keeps them literal under string.Format.
                    format.Append(text.TextToken.ValueText);
                }
                else if (c is InterpolationSyntax ip)
                {
                    format.Append('{').Append(args.Count);
                    if (ip.AlignmentClause != null)
                    {
                        var constVal = m_Model.GetConstantValue(ip.AlignmentClause.Value);
                        if (!constVal.HasValue || !(constVal.Value is int alignInt))
                            throw new NotSupportedException("Interpolation alignment must be a constant int");
                        format.Append(',').Append(alignInt);
                    }
                    if (ip.FormatClause != null)
                    {
                        format.Append(':').Append(ip.FormatClause.FormatStringToken.ValueText);
                    }
                    format.Append('}');

                    var inner = VisitExpression(ip.Expression, null);
                    if (inner.Type != typeof(object))
                        inner = Expression.Convert(inner, typeof(object));
                    args.Add(inner);
                }
                else
                {
                    throw new NotSupportedException($"Interpolated content {c.Kind()} not supported");
                }
            }
            var argsArray = Expression.NewArrayInit(typeof(object), args);
            return Expression.Call(s_StringFormat, Expression.Constant(format.ToString(), typeof(string)), argsArray);
        }

        private Expression VisitEventSubscribe(MemberAccessExpressionSyntax mae, ExpressionSyntax handler, IEventSymbol evSym, bool isAdd)
        {
            var containing = ResolveTypeSymbol(evSym.ContainingType);
            var bf = BindingFlags.Public | BindingFlags.NonPublic |
                     (evSym.IsStatic ? BindingFlags.Static : BindingFlags.Instance);
            var ev = containing.GetEvent(evSym.Name, bf);
            if (ev == null)
                throw new InvalidOperationException($"Event {evSym.Name} not found on {containing.FullName}");
            var handlerType = ev.EventHandlerType;
            var handlerExpr = VisitExpression(handler, handlerType);
            if (handlerExpr.Type != handlerType)
                handlerExpr = Expression.Convert(handlerExpr, handlerType);
            var accessor = isAdd ? ev.GetAddMethod() : ev.GetRemoveMethod();
            Expression receiver = evSym.IsStatic ? null : VisitExpression(mae.Expression, null);
            return Expression.Call(receiver, accessor, handlerExpr);
        }

        // x op= rhs  =>  x = x op rhs. Reads LHS via the same identifier
        // resolution as RHS to keep slot vs local rules in one place.
        private Expression LowerCompoundAssign(AssignmentExpressionSyntax assign, IdentifierNameSyntax lhsId)
        {
            var lhsRead = VisitIdentifier(lhsId);

            // ??= is special: only write when LHS is null.
            if (assign.Kind() == SyntaxKind.CoalesceAssignmentExpression)
            {
                var rhsCoal = VisitExpression(assign.Right, lhsRead.Type);
                if (rhsCoal.Type != lhsRead.Type && lhsRead.Type.IsAssignableFrom(rhsCoal.Type))
                    rhsCoal = Expression.Convert(rhsCoal, lhsRead.Type);
                var nullLit = Expression.Constant(null, lhsRead.Type);
                var whenNull = WriteIdentifier(lhsId.Identifier.ValueText, rhsCoal);
                // `x ??= rhs` returns the final value of x.
                return Expression.Condition(
                    Expression.Equal(lhsRead, nullLit),
                    whenNull,
                    lhsRead);
            }

            var rhs = VisitExpression(assign.Right, lhsRead.Type);
            if (rhs.Type != lhsRead.Type)
            {
                if (lhsRead.Type.IsAssignableFrom(rhs.Type))
                    rhs = Expression.Convert(rhs, lhsRead.Type);
            }
            Expression combined = assign.Kind() switch
            {
                SyntaxKind.AddAssignmentExpression => Expression.Add(lhsRead, rhs),
                SyntaxKind.SubtractAssignmentExpression => Expression.Subtract(lhsRead, rhs),
                SyntaxKind.MultiplyAssignmentExpression => Expression.Multiply(lhsRead, rhs),
                SyntaxKind.DivideAssignmentExpression => Expression.Divide(lhsRead, rhs),
                SyntaxKind.ModuloAssignmentExpression => Expression.Modulo(lhsRead, rhs),
                SyntaxKind.AndAssignmentExpression => Expression.And(lhsRead, rhs),
                SyntaxKind.OrAssignmentExpression => Expression.Or(lhsRead, rhs),
                SyntaxKind.ExclusiveOrAssignmentExpression => Expression.ExclusiveOr(lhsRead, rhs),
                SyntaxKind.LeftShiftAssignmentExpression => Expression.LeftShift(lhsRead, rhs),
                SyntaxKind.RightShiftAssignmentExpression => Expression.RightShift(lhsRead, rhs),
                _ => throw new NotSupportedException($"Compound op {assign.Kind()} not supported"),
            };
            return WriteIdentifier(lhsId.Identifier.ValueText, combined);
        }

        // Helper used by both simple and compound assignment: writes `value`
        // into whatever named target — local lambda-scope var or slot.
        private Expression WriteIdentifier(string name, Expression value)
        {
            foreach (var scope in m_LambdaScopes)
            {
                if (scope.TryGetValue(name, out var local))
                {
                    var v = value;
                    if (v.Type != local.Type)
                    {
                        if (local.Type.IsAssignableFrom(v.Type)) v = Expression.Convert(v, local.Type);
                        else throw new NotSupportedException($"Cannot assign {v.Type.Name} to local '{name}' of type {local.Type.Name}");
                    }
                    return Expression.Assign(local, v);
                }
            }
            var slotType = LookupSlotType(name);
            if (slotType == null)
                throw new InvalidOperationException($"Cannot assign to '{name}': not declared.");
            if (value.Type != slotType)
            {
                if (slotType.IsAssignableFrom(value.Type)) value = Expression.Convert(value, slotType);
                else throw new LiteCompilerException(
                    "E_SESSION_REDECLARE_TYPE_MISMATCH",
                    $"Slot '{name}' is {slotType.Name}, cannot assign value of type {value.Type.Name}.");
            }
            var tmp = Expression.Parameter(slotType, "tmp_" + name);
            var boxed = slotType.IsValueType
                ? (Expression)Expression.Convert(tmp, typeof(object))
                : (Expression)tmp;
            return Expression.Block(
                slotType,
                new[] { tmp },
                Expression.Assign(tmp, value),
                Expression.Call(m_SlotsExpr, s_DictSet, Expression.Constant(name), boxed),
                tmp);
        }

        // Assignment to an existing slot or local: `x = 100;` or `x += 5;`.
        // For compound ops we read LHS, apply the binary op, then assign.
        // Destructuring LHS: `(a, b) = ...` or `(int a, int b) = ...` or `var (a, b) = ...`.
        private Expression VisitAssignment(AssignmentExpressionSyntax assign)
        {
            if (assign.Kind() == SyntaxKind.SimpleAssignmentExpression)
            {
                if (assign.Left is TupleExpressionSyntax tupLhs)
                    return VisitTupleDeconstruct(tupLhs, assign.Right);
                if (assign.Left is DeclarationExpressionSyntax declExp
                    && declExp.Designation is ParenthesizedVariableDesignationSyntax pvd)
                    return VisitVarTupleDeconstruct(declExp.Type, pvd, assign.Right);
            }

            // Event += / -= on a member access (e.g. obj.Click += handler).
            if (assign.Left is MemberAccessExpressionSyntax mae
                && (assign.IsKind(SyntaxKind.AddAssignmentExpression)
                    || assign.IsKind(SyntaxKind.SubtractAssignmentExpression)))
            {
                var leftSym = m_Model.GetSymbolInfo(mae).Symbol;
                if (leftSym is IEventSymbol evSym)
                    return VisitEventSubscribe(mae, assign.Right, evSym, assign.IsKind(SyntaxKind.AddAssignmentExpression));
            }

            // Fail-fast: mutation of a field/property on a value-type session slot.
            // `s.X = 5` where s is a boxed value-type slot writes to the boxed
            // copy and the unboxed write is lost — silent corruption. Reject.
            if (assign.Left is MemberAccessExpressionSyntax lhsMae)
            {
                var rootType = SessionSlotRootType(lhsMae);
                if (rootType != null && rootType.IsValueType)
                {
                    ExpressionSyntax cur = lhsMae;
                    while (cur is MemberAccessExpressionSyntax m) cur = m.Expression;
                    var rootName = ((IdentifierNameSyntax)cur).Identifier.ValueText;
                    throw new LiteCompilerException(
                        "E_SESSION_VALUETYPE_MUTATION",
                        $"Cannot mutate field/property '{lhsMae.Name.Identifier.ValueText}' of value-type session variable '{rootName}' ({rootType.Name}); Lite mode stores value types as boxed copies. " +
                        $"Reassign the whole value: '{rootName} = new {rootType.Name} {{ ... }};'");
                }

                // Reference-type root: emit a property setter / field assign.
                // Only simple-assignment form; compound-assign on member access
                // is not lowered yet (would need ldfld + binop + stfld pattern).
                if (assign.Kind() == SyntaxKind.SimpleAssignmentExpression)
                {
                    var lhsSym = m_Model.GetSymbolInfo(lhsMae).Symbol;
                    if (lhsSym is IPropertySymbol propSym)
                    {
                        var propInfo = ResolveProperty(propSym);
                        if (propInfo.SetMethod == null)
                            throw new InvalidOperationException($"Property '{propSym.Name}' has no setter on {propInfo.DeclaringType.FullName}");
                        var propReceiver = propInfo.GetMethod != null && propInfo.GetMethod.IsStatic
                            ? null
                            : VisitExpression(lhsMae.Expression, null);
                        var propRhs = VisitExpression(assign.Right, propInfo.PropertyType);
                        if (propRhs.Type != propInfo.PropertyType && propInfo.PropertyType.IsAssignableFrom(propRhs.Type))
                            propRhs = Expression.Convert(propRhs, propInfo.PropertyType);
                        return Expression.Assign(Expression.Property(propReceiver, propInfo), propRhs);
                    }
                    if (lhsSym is IFieldSymbol fieldSym)
                    {
                        var fieldInfo = ResolveField(fieldSym);
                        var fieldReceiver = fieldInfo.IsStatic
                            ? null
                            : VisitExpression(lhsMae.Expression, null);
                        var fieldRhs = VisitExpression(assign.Right, fieldInfo.FieldType);
                        if (fieldRhs.Type != fieldInfo.FieldType && fieldInfo.FieldType.IsAssignableFrom(fieldRhs.Type))
                            fieldRhs = Expression.Convert(fieldRhs, fieldInfo.FieldType);
                        return Expression.Assign(Expression.Field(fieldReceiver, fieldInfo), fieldRhs);
                    }
                }
            }

            if (!(assign.Left is IdentifierNameSyntax lhsId))
                throw new NotSupportedException($"Assignment to {assign.Left.Kind()} not supported in Lite mode");
            if (assign.Kind() != SyntaxKind.SimpleAssignmentExpression)
                return LowerCompoundAssign(assign, lhsId);

            var name = lhsId.Identifier.ValueText;
            // Local scopes (lambda params, for-init vars, catch params, foreach
            // loop vars) come first per C# scoping. Local assignment translates
            // to a regular Expression.Assign on the ParameterExpression.
            foreach (var scope in m_LambdaScopes)
            {
                if (scope.TryGetValue(name, out var local))
                {
                    var rhsLocal = VisitExpression(assign.Right, local.Type);
                    if (rhsLocal.Type != local.Type)
                    {
                        if (local.Type.IsAssignableFrom(rhsLocal.Type))
                            rhsLocal = Expression.Convert(rhsLocal, local.Type);
                        else
                            throw new NotSupportedException($"Cannot assign {rhsLocal.Type.Name} to local '{name}' of type {local.Type.Name}");
                    }
                    return Expression.Assign(local, rhsLocal);
                }
            }

            var slotType = LookupSlotType(name);
            if (slotType == null)
                throw new InvalidOperationException($"Cannot assign to '{name}': not declared as a slot. Use 'var {name} = ...' to declare first.");

            var rhs = VisitExpression(assign.Right, slotType);
            if (rhs.Type != slotType)
            {
                if (slotType.IsAssignableFrom(rhs.Type))
                    rhs = Expression.Convert(rhs, slotType);
                else
                    throw new LiteCompilerException(
                        "E_SESSION_REDECLARE_TYPE_MISMATCH",
                        $"Slot '{name}' is {slotType.Name}, cannot assign value of type {rhs.Type.Name}.");
            }

            // Evaluate RHS once into a temp, write to slot, return the value.
            // This preserves C# `x = expr` returning the assigned value.
            var tmp = Expression.Parameter(slotType, "tmp_" + name);
            var boxed = slotType.IsValueType
                ? (Expression)Expression.Convert(tmp, typeof(object))
                : (Expression)tmp;
            return Expression.Block(
                slotType,
                new[] { tmp },
                Expression.Assign(tmp, rhs),
                Expression.Call(m_SlotsExpr, s_DictSet, Expression.Constant(name), boxed),
                tmp);
        }

        // Maps an InvocationExpression's args to a MethodInfo's parameter slots,
        // honoring named args (NameColon), params (last param is T[] with
        // ParamArrayAttribute), and optional args (HasDefaultValue).
        // skipFirstParam=true for extension-method reduced calls where param[0]
        // is the receiver injected by VisitInvocation.
        private Expression[] BindArguments(SeparatedSyntaxList<ArgumentSyntax> argList, ParameterInfo[] methodParams, bool skipFirstParam)
        {
            int start = skipFirstParam ? 1 : 0;
            int slotCount = methodParams.Length - start;
            var result = new Expression[slotCount];
            bool[] filled = new bool[slotCount];

            int lastIdx = slotCount - 1;
            bool hasParams = slotCount > 0
                && methodParams[methodParams.Length - 1].GetCustomAttribute<ParamArrayAttribute>() != null;
            List<Expression> paramsAccum = null;
            int posIdx = 0;

            for (int i = 0; i < argList.Count; i++)
            {
                var a = argList[i];
                int slot;
                if (a.NameColon != null)
                {
                    var paramName = a.NameColon.Name.Identifier.ValueText;
                    slot = -1;
                    for (int k = start; k < methodParams.Length; k++)
                        if (methodParams[k].Name == paramName) { slot = k - start; break; }
                    if (slot < 0) throw new InvalidOperationException($"No parameter named '{paramName}'");
                }
                else
                {
                    slot = posIdx++;
                }

                if (hasParams && slot == lastIdx && a.NameColon == null)
                {
                    var elemType = methodParams[start + lastIdx].ParameterType.GetElementType();
                    paramsAccum ??= new List<Expression>();
                    paramsAccum.Add(VisitExpression(a.Expression, elemType));
                }
                else if (!a.RefKindKeyword.IsKind(SyntaxKind.None))
                {
                    // ref/out arg. LiteREPLCompiler-slot case is already rejected by the
                    // VisitInvocation BYREF fail-fast scan, so here we expect
                    // either an inline `out int n` declaration or a lambda-scoped
                    // identifier. Either way the call expects an l-value of the
                    // unwrapped (non-ByRef) element type.
                    var paramType = methodParams[start + slot].ParameterType;
                    var unwrapType = paramType.IsByRef ? paramType.GetElementType() : paramType;
                    Expression refArg;
                    if (a.Expression is DeclarationExpressionSyntax decl
                        && decl.Designation is SingleVariableDesignationSyntax svd)
                    {
                        // `out int n` — create a fresh ParameterExpression and
                        // hoist it via the submission-scope so subsequent code in
                        // this submission can read it. The body Block declares the
                        // accumulated out-vars in CompileToLambda.
                        var varName = svd.Identifier.ValueText;
                        var declType = decl.Type.IsVar
                            ? unwrapType
                            : ResolveTypeSymbol(m_Model.GetTypeInfo(decl.Type).Type);
                        var p = Expression.Parameter(declType, varName);
                        m_SubmissionScope[varName] = p;
                        m_SubmissionOutVars.Add(p);
                        refArg = p;
                    }
                    else if (a.Expression is IdentifierNameSyntax id)
                    {
                        // `out existing` — must already be in lambda/submission scope.
                        ParameterExpression existing = null;
                        foreach (var scope in m_LambdaScopes)
                            if (scope.TryGetValue(id.Identifier.ValueText, out var p)) { existing = p; break; }
                        if (existing == null)
                            throw new InvalidOperationException($"ref/out argument '{id.Identifier.ValueText}' not in scope (slot was rejected earlier; expected a local)");
                        refArg = existing;
                    }
                    else
                    {
                        throw new NotSupportedException($"ref/out argument expression {a.Expression.Kind()} not supported");
                    }
                    result[slot] = refArg;
                    filled[slot] = true;
                }
                else
                {
                    result[slot] = VisitExpression(a.Expression, methodParams[start + slot].ParameterType);
                    filled[slot] = true;
                }
            }

            if (paramsAccum != null)
            {
                var elemType = methodParams[start + lastIdx].ParameterType.GetElementType();
                result[lastIdx] = Expression.NewArrayInit(elemType, paramsAccum);
                filled[lastIdx] = true;
            }

            for (int i = 0; i < slotCount; i++)
            {
                if (!filled[i])
                {
                    var pi = methodParams[start + i];
                    if (pi.HasDefaultValue)
                    {
                        result[i] = Expression.Constant(pi.DefaultValue, pi.ParameterType);
                    }
                    else if (hasParams && i == lastIdx)
                    {
                        result[i] = Expression.NewArrayInit(pi.ParameterType.GetElementType());
                    }
                    else
                    {
                        throw new InvalidOperationException($"Parameter '{pi.Name}' is not bound and has no default");
                    }
                }
            }
            return result;
        }

        // ---------- member resolution ----------
        private static MethodInfo ResolveMethod(IMethodSymbol m)
        {
            var containing = ResolveTypeSymbol(m.ContainingType);
            var bf = BindingFlags.Public | BindingFlags.NonPublic |
                     (m.IsStatic ? BindingFlags.Static : BindingFlags.Instance);

            if (m.IsGenericMethod && m.TypeArguments.Length > 0)
                return ResolveGenericMethod(m, containing, bf);

            var paramTypes = m.Parameters.Select(ResolveParamReflectionType).ToArray();
            var info = containing.GetMethod(m.Name, bf, binder: null, types: paramTypes, modifiers: null);
            if (info == null)
                throw new InvalidOperationException($"Cannot resolve method {m.Name}({string.Join(",", paramTypes.Select(t => t.Name))}) on {containing.FullName}");
            return info;
        }

        // ResolveTypeSymbol applied to a parameter, plus MakeByRefType when the
        // parameter is `ref`/`out`/`in` — the reflected MethodInfo signature uses
        // `T&` for those slots, so a plain Int32 lookup would miss TryParse etc.
        private static Type ResolveParamReflectionType(IParameterSymbol p)
        {
            var t = ResolveTypeSymbol(p.Type);
            return p.RefKind == RefKind.None ? t : t.MakeByRefType();
        }

        // Find an open generic method definition by name + type-arity +
        // parameter count, then MakeGenericMethod, then verify the closed
        // parameter shape matches the constructed IMethodSymbol's parameters
        // (handles LINQ-style overloads like Where(IEnumerable<T>,Func<T,bool>)
        // vs Where(IEnumerable<T>,Func<T,int,bool>)).
        private static MethodInfo ResolveGenericMethod(IMethodSymbol m, Type containing, BindingFlags bf)
        {
            var typeArgs = m.TypeArguments.Select(ResolveTypeSymbol).ToArray();
            var constructedParamTypes = m.Parameters.Select(ResolveParamReflectionType).ToArray();
            foreach (var mi in containing.GetMethods(bf))
            {
                if (mi.Name != m.Name) continue;
                if (!mi.IsGenericMethodDefinition) continue;
                if (mi.GetGenericArguments().Length != typeArgs.Length) continue;
                var miPs = mi.GetParameters();
                if (miPs.Length != constructedParamTypes.Length) continue;
                MethodInfo closed;
                try { closed = mi.MakeGenericMethod(typeArgs); }
                catch { continue; }
                var closedPs = closed.GetParameters();
                bool ok = true;
                for (int i = 0; i < constructedParamTypes.Length; i++)
                    if (closedPs[i].ParameterType != constructedParamTypes[i]) { ok = false; break; }
                if (ok) return closed;
            }
            throw new InvalidOperationException(
                $"Cannot resolve generic method {m.Name}<{string.Join(",", typeArgs.Select(t => t.Name))}> on {containing.FullName}");
        }

        private static PropertyInfo ResolveProperty(IPropertySymbol p)
        {
            var containing = ResolveTypeSymbol(p.ContainingType);
            var bf = BindingFlags.Public | BindingFlags.NonPublic |
                     (p.IsStatic ? BindingFlags.Static : BindingFlags.Instance);
            var info = containing.GetProperty(p.Name, bf);
            if (info == null)
                throw new InvalidOperationException($"Cannot resolve property {p.Name} on {containing.FullName}");
            return info;
        }

        private static FieldInfo ResolveField(IFieldSymbol f)
        {
            var containing = ResolveTypeSymbol(f.ContainingType);
            var bf = BindingFlags.Public | BindingFlags.NonPublic |
                     (f.IsStatic ? BindingFlags.Static : BindingFlags.Instance);
            var info = containing.GetField(f.Name, bf);
            if (info == null)
                throw new InvalidOperationException($"Cannot resolve field {f.Name} on {containing.FullName}");
            return info;
        }

        private static ConstructorInfo ResolveConstructor(IMethodSymbol m)
        {
            var containing = ResolveTypeSymbol(m.ContainingType);
            var paramTypes = m.Parameters.Select(p => ResolveTypeSymbol(p.Type)).ToArray();
            var info = containing.GetConstructor(paramTypes);
            if (info == null)
                throw new InvalidOperationException($"Cannot resolve constructor({string.Join(",", paramTypes.Select(t => t.Name))}) on {containing.FullName}");
            return info;
        }

        // ---------- type resolution ----------
        private Type ResolveDeclaredType(VariableDeclarationSyntax decl)
        {
            if (decl.Type.IsVar) return null;
            var sym = m_Model.GetTypeInfo(decl.Type).Type;
            if (sym == null) throw new InvalidOperationException($"Cannot resolve {decl.Type}");
            return ResolveTypeSymbol(sym);
        }

        private static Type ResolveTypeSymbol(ITypeSymbol sym)
        {
            var prim = TryGetPrimitive(sym.SpecialType);
            if (prim != null) return prim;

            // C# tuple types ((int, int)) — the symbol's display form is
            // "(T1, T2)" which is not a CLR metadata name; map to ValueTuple<>.
            if (sym is INamedTypeSymbol tupSym && tupSym.IsTupleType)
            {
                var tupArgs = tupSym.TupleElements.IsDefault
                    ? tupSym.TypeArguments.Select(ResolveTypeSymbol).ToArray()
                    : tupSym.TupleElements.Select(e => ResolveTypeSymbol(e.Type)).ToArray();
                int arity = tupArgs.Length;
                var open = Type.GetType($"System.ValueTuple`{arity}")
                    ?? AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType($"System.ValueTuple`{arity}"))
                        .FirstOrDefault(t => t != null);
                if (open == null) throw new InvalidOperationException($"Cannot find System.ValueTuple`{arity}");
                return open.MakeGenericType(tupArgs);
            }

            if (sym is INamedTypeSymbol named && named.IsGenericType)
            {
                var openName = named.ConstructedFrom.ToDisplayString(s_TypeNameFormat);
                var metadataName = openName + "`" + named.Arity;
                var openType = Type.GetType(metadataName)
                    ?? AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType(metadataName))
                        .FirstOrDefault(t => t != null);
                if (openType == null)
                    throw new InvalidOperationException($"Cannot find open generic type {metadataName}");
                var typeArgs = named.TypeArguments.Select(ResolveTypeSymbol).ToArray();
                return openType.MakeGenericType(typeArgs);
            }

            var full = sym.ToDisplayString(s_TypeNameFormat);
            var found = Type.GetType(full)
                ?? AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType(full))
                    .FirstOrDefault(x => x != null);
            if (found == null) throw new InvalidOperationException($"Cannot resolve type '{full}'");
            return found;
        }

        private static Type TryGetPrimitive(SpecialType st)
        {
            switch (st)
            {
                case SpecialType.System_Boolean: return typeof(bool);
                case SpecialType.System_Byte: return typeof(byte);
                case SpecialType.System_SByte: return typeof(sbyte);
                case SpecialType.System_Int16: return typeof(short);
                case SpecialType.System_UInt16: return typeof(ushort);
                case SpecialType.System_Int32: return typeof(int);
                case SpecialType.System_UInt32: return typeof(uint);
                case SpecialType.System_Int64: return typeof(long);
                case SpecialType.System_UInt64: return typeof(ulong);
                case SpecialType.System_Single: return typeof(float);
                case SpecialType.System_Double: return typeof(double);
                case SpecialType.System_Decimal: return typeof(decimal);
                case SpecialType.System_Char: return typeof(char);
                case SpecialType.System_String: return typeof(string);
                case SpecialType.System_Object: return typeof(object);
                case SpecialType.System_Void: return typeof(void);
                default: return null;
            }
        }
    }
}
