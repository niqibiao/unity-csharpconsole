using System;

namespace Zh1Zh1.CSharpConsole.Service
{
    [Serializable]
    internal sealed class SessionStateInfo
    {
        public string sessionId;
        public bool hasCompiler;
        public bool hasExecutor;
    }
}
