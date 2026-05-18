using System.Collections.Generic;
using System.Linq.Expressions;

namespace Zh1Zh1.CSharpConsole.Lite
{
    // Editor-side compiler abstraction for the Lite-mode path. The Editor
    // implementation (LiteREPLCompiler) lives in the Editor assembly because
    // it needs Roslyn; the HTTP service in Runtime calls it through this
    // interface so the Runtime assembly stays Roslyn-free.
    //
    // Slots is exposed so the caller can construct a LiteWireWriter with the
    // same dictionary instance the compiler embeds via Expression.Constant —
    // the writer uses ReferenceEquals to detect SlotsRef.
    public interface ILiteCompiler
    {
        // defaultUsing is the client-supplied prefix (e.g. "using UnityEditor;\n")
        // that gets concatenated onto the compiler's built-in defaults. Empty
        // string is fine — the compiler always brings its own minimal set.
        Expression<System.Func<object>> CompileToLambda(string code, string defaultUsing = "");
        IDictionary<string, object> Slots { get; }
    }
}
