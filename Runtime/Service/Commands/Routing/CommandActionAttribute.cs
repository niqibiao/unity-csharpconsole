using System;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Routing
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class CommandActionAttribute : Attribute
    {
        public string commandNamespace { get; }
        public string action { get; }
        public bool editorOnly { get; }
        public bool runOnMainThread { get; }
        public string summary { get; }

        // Default runOnMainThread: true so user extensions are safe by default.
        public CommandActionAttribute(
            string commandNamespace,
            string action,
            bool editorOnly = false,
            bool runOnMainThread = true,
            string summary = "")
        {
            this.commandNamespace = commandNamespace ?? "";
            this.action = action ?? "";
            this.editorOnly = editorOnly;
            this.runOnMainThread = runOnMainThread;
            this.summary = summary ?? "";
        }
    }
}
