using System;

namespace Zh1Zh1.CSharpConsole.Service
{
    [Serializable]
    internal class HttpResponseEnvelope
    {
        public bool ok;
        public string stage = "";
        public string type = "";
        public string summary = "";
        public string sessionId = "";
        public string dataJson = "{}";
    }

    [Serializable]
    internal class TextResponseData
    {
        public string text = "";
    }

}
