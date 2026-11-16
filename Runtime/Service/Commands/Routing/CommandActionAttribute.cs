using System;
using UnityEngine.Scripting;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Routing
{
    /// <summary>
    /// Derives from <see cref="PreserveAttribute"/> because handlers are reached only
    /// through reflection: the linker sees no call to them and strips them from a
    /// player whose assembly is not a root. A method marked with an attribute derived
    /// from PreserveAttribute is kept wherever the linker processes its assembly, so
    /// command authors need no [Preserve] or link.xml of their own.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class CommandActionAttribute : PreserveAttribute
    {
        public string commandNamespace { get; }
        public string action { get; }
        public bool editorOnly { get; }
        public bool runOnMainThread { get; }
        public string summary { get; }
        public Type resultType { get; }
        public bool requiresSessionId { get; }

        // Default runOnMainThread: true so user extensions are safe by default.
        public CommandActionAttribute(
            string commandNamespace,
            string action,
            bool editorOnly = false,
            bool runOnMainThread = true,
            string summary = "",
            Type resultType = null,
            bool requiresSessionId = false)
        {
            this.commandNamespace = commandNamespace ?? "";
            this.action = action ?? "";
            this.editorOnly = editorOnly;
            this.runOnMainThread = runOnMainThread;
            this.summary = summary ?? "";
            this.resultType = resultType;
            this.requiresSessionId = requiresSessionId;
        }
    }
}
