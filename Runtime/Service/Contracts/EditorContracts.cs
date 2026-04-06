using System;

namespace Zh1Zh1.CSharpConsole.Service
{
    [Serializable]
    internal class EditorREPLRequest
    {
        public string content = "";
        public string defines = "";
        public string defaultUsing = "";
        public string uuid = "";
        public bool reset;
    }

    [Serializable]
    internal class CompileREPLRequest
    {
        public string content = "";
        public string defines = "";
        public string defaultUsing = "";
        public string uuid = "";
        public string targetIP = "";
        public string targetPort = "";
        public string runtimeDllPath = "";
        public bool reset;
    }

    [Serializable]
    internal class CompletionRequest
    {
        public string uuid = "";
        public string code = "";
        public int cursorPosition;
        public string defines = "";
        public string defaultUsing = "";
        public string runtimeDllPath = "";
    }

    [Serializable]
    internal class CompletionItemJson
    {
        public string label = "";
        public string kind = "";
        public string detail = "";
        public string accessibility = "";
    }

    [Serializable]
    internal class CompletionResponse
    {
        public CompletionItemJson[] items = Array.Empty<CompletionItemJson>();
        public string error = "";
    }

    [Serializable]
    internal class UploadDllsResponse
    {
        public string runtimeDllPath = "";
        public string runtimeDefinesPath = "";
        public string error = "";
    }

    [Serializable]
    internal class CompileOnlyResponse
    {
        public string dllBase64 = "";
        public string className = "";
        public string error = "";
    }
}
