using System;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Core
{
    [Serializable]
    public class CommandRequest
    {
        [Serializable]
        public sealed class InvocationCommand
        {
            public string commandNamespace = "";
            public string action = "";
        }

        [Serializable]
        public sealed class InvocationPayload
        {
            public string sessionId = "";
            public string argsJson = "";
            public InvocationCommand command;
        }

        public InvocationPayload invocation;
    }
}
