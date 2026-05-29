using System;
#if !CSHARPCONSOLE_LITE_DISABLED
using Zh1Zh1.CSharpConsole.Lite;
#endif

namespace Zh1Zh1.CSharpConsole.Service
{
    [Serializable]
    internal class ExecuteREPLRequest
    {
        public string uuid = "";
        public bool reset;

        // HybridCLR path payload (Player loads via Assembly.Load).
        public string dllBase64 = "";
        public string className = "";

        // Lite path payload — Docs~/ExpressionInterpreterFeasibility_zh.md §3.1.
        // Player dispatches on bodyBinary non-empty → Lite, else dllBase64 → HybridCLR.
        public string bodyBinary = "";
#if !CSHARPCONSOLE_LITE_DISABLED
        public TypeRegEntryDto[] typeReg = Array.Empty<TypeRegEntryDto>();
#endif
        public int registryEpoch;
    }

    [Serializable]
    internal class ForwardResetRequest
    {
        public string uuid = "";
        public bool reset;
    }

    [Serializable]
    internal class ExecuteResponse
    {
        public string result = "";
        public string error = "";
    }
}
