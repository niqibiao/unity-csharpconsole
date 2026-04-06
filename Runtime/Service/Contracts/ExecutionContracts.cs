using System;

namespace Zh1Zh1.CSharpConsole.Service
{
    [Serializable]
    internal class ExecuteREPLRequest
    {
        public string uuid = "";
        public bool reset;
        public string dllBase64 = "";
        public string className = "";
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
