using System;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Core
{
    [Serializable]
    internal sealed class CommandDescriptor
    {
        public string id = "";
        public string commandNamespace = "";
        public string action = "";
        public string summary = "";
        public bool editorOnly;
        public bool runOnMainThread;
        public bool requiresProtectedInvocation;
        public bool allowInBatch = true;
        public string declaringType = "";
        public string methodName = "";
        public string commandType = "builtin";
        public CommandArgumentDescriptor[] arguments = Array.Empty<CommandArgumentDescriptor>();
    }
}
