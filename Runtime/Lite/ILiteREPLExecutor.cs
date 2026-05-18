using System.Threading.Tasks;

namespace Zh1Zh1.CSharpConsole.Lite
{
    // Player-side executor for the Lite-mode binary Expression protocol
    // (Docs~/ExpressionInterpreterFeasibility_zh.md §3.1 v3).
    //
    // Distinct from IREPLExecutor (HybridCLR Assembly.Load path). The Player
    // dispatches by inspecting which payload the editor sent: bodyBinary
    // non-empty → ILiteREPLExecutor; dllBase64 non-empty → IREPLExecutor.
    public interface ILiteREPLExecutor
    {
        // Execute one Lite-mode submission.
        //   bodyBinary    — LiteWireWriter output (the editor's serialized
        //                   Expression tree)
        //   typeRegDelta  — new (id, AQN) pairs introduced by this submission,
        //                   matched to the writer's delta buffer drain
        //   envelopeEpoch — writer's registry epoch at serialization time
        // Returns a LiteExecuteOutcome whose ErrorCode is empty on success.
        // If the envelope's epoch does not match the executor's local epoch,
        // returns NeedsResync=true and does not execute the body.
        Task<LiteExecuteOutcome> ExecuteAsync(byte[] bodyBinary,
            TypeRegEntryDto[] typeRegDelta,
            int envelopeEpoch);

        // Drop local session state: clear slots, clear registry, bump epoch.
        // Called when the client requests a reset (REPL :reset) or eviction.
        void Reset();
    }

    public sealed class LiteExecuteOutcome
    {
        public string Result = "";
        public string ErrorCode = "";
        public bool NeedsResync;
        public int ServerEpoch;
    }
}
