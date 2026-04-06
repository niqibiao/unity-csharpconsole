using UnityEngine;

namespace Zh1Zh1.CSharpConsole.Service.Internal
{
    internal sealed class HttpEnvelopeFactory
    {
        public HttpResponseEnvelope CreateEnvelope(bool ok, string stage, string type, string summary, string sessionId, string dataJson)
        {
            return new HttpResponseEnvelope
            {
                ok = ok,
                stage = stage ?? "",
                type = type ?? "",
                summary = summary ?? "",
                sessionId = sessionId ?? "",
                dataJson = string.IsNullOrWhiteSpace(dataJson) ? "{}" : dataJson
            };
        }

        public HttpResponseEnvelope CreateTextEnvelope(string stage, string text, string sessionId)
        {
            var normalized = text ?? string.Empty;
            var trimmed = normalized.Trim();
            var lowered = trimmed.ToLowerInvariant();
            var ok = true;
            var resultType = "ok";
            var summary = trimmed;

            if (string.IsNullOrEmpty(trimmed))
            {
                summary = "OK";
            }
            else if (lowered.StartsWith("compile failed"))
            {
                ok = false;
                resultType = "compile_error";
            }
            else if (lowered.Contains("forward failed") || lowered.StartsWith("timeout:"))
            {
                ok = false;
                resultType = "runtime_error";
                stage = "execute";
            }
            else if (lowered.Contains("error post:"))
            {
                ok = false;
                resultType = "system_error";
                stage = "unknown";
            }
            else if (lowered.Contains("exception") || lowered.Contains("load error:") || lowered.Contains("execution error:"))
            {
                ok = false;
                resultType = "runtime_error";
            }

            var dataJson = JsonUtility.ToJson(new TextResponseData { text = normalized });
            return CreateEnvelope(ok, stage, resultType, summary, sessionId, dataJson);
        }

    }
}
