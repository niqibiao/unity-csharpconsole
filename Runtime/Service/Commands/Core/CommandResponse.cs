using System;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Core
{
    [Serializable]
    public class CommandResponse
    {
        public bool ok;
        public string type = "";
        public string summary = "";
        public string commandNamespace = "";
        public string action = "";
        public string sessionId = "";
        public string resultJson = "";
    }
}
