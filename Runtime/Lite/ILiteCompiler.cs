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
        // Builds the submission lambda WITHOUT committing session state. The
        // caller serializes + forwards to the player, then calls Commit() on the
        // returned handle only after the player confirms successful execution —
        // this keeps editor SlotTypes / Roslyn chain from diverging from the
        // player slot dictionary when player execution or transport fails.
        //
        // defaultUsing is the client-supplied prefix (e.g. "using UnityEditor;\n")
        // that gets concatenated onto the compiler's built-in defaults. Empty
        // string is fine — the compiler always brings its own minimal set.
        IPreparedLiteSubmission PrepareSubmission(string code, string defaultUsing = "");
        IDictionary<string, object> Slots { get; }
    }

    // A compiled-but-uncommitted Lite submission. Commit() promotes the
    // submission's declared session-slot types and advances the Roslyn
    // submission chain; it MUST be called only after the player has executed
    // the body successfully, and is a no-op if called more than once.
    public interface IPreparedLiteSubmission
    {
        Expression<System.Func<object>> Lambda { get; }
        void Commit();
    }
}
