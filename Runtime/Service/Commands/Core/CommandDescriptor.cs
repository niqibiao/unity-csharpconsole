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
        public string declaringType = "";
        public string methodName = "";
        public CommandArgumentDescriptor[] arguments = Array.Empty<CommandArgumentDescriptor>();
    }
}
