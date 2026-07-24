using System;

namespace Zh1Zh1.CSharpConsole.Service
{
    [Serializable]
    internal sealed class InvocationReceipt
    {
        public string invocationId = "";
        public string targetId = "";
        public string serviceEpoch = "";
        public string endpoint = "";
        public string requestDigest = "";
        public string state = "none";
        public string guarantee = "none";
        public bool replayed;
        public int dedupeWindowSeconds;
        public string createdAtUtc = "";
        public string updatedAtUtc = "";
    }

    [Serializable]
    internal sealed class InvocationStatusRequest
    {
        public string invocationId = "";
        public string targetId = "";
    }

    [Serializable]
    internal sealed class InvocationStatusResponse
    {
        public bool found;
        public string invocationId = "";
        public string targetId = "";
        public string serviceEpoch = "";
        public string endpoint = "";
        public string requestDigest = "";
        public string state = "";
        public bool protectionExpired;
        public string previousState = "";
        public string createdAtUtc = "";
        public string updatedAtUtc = "";
        public string responseJson = "";
    }
}
