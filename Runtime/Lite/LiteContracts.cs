using System;

namespace Zh1Zh1.CSharpConsole.Lite
{
    // Wire-side DTO for SessionTypeRegistry's (id, AQN) pairs — JsonUtility-
    // compatible counterpart to TypeRegEntry. Carried in ExecuteREPLRequest.typeReg
    // (the envelope field) as the per-submission delta from writer to reader.
    [Serializable]
    public class TypeRegEntryDto
    {
        public int id;
        public string aqn = "";
    }

    // Lite-mode envelope data payload, serialized into HttpResponseEnvelope.dataJson
    // for /execute responses when the request used the Lite path. Distinct from
    // TextResponseData (HybridCLR path) so the client can decide which DTO to
    // deserialize by inspecting the envelope's `type` field.
    //
    // needsResync: Player sets this when its local registry epoch differs from the
    //   envelope's registryEpoch (or when Resolve hits an unknown id). Editor must
    //   respond by calling SessionTypeRegistry.PrepareResync() and resending the
    //   full registry in the next /execute envelope's typeReg.
    // serverEpoch: Player's current epoch. Editor uses this to detect player
    //   restart (epoch goes back to 0 when player rebooted).
    // errorCode: structured error code from LiteWireException or executor
    //   (e.g. "E_TYPEREG_UNKNOWN_ID", "E_LITE_CONSTANT_NONSCALAR"). Empty on success.
    [Serializable]
    internal class LiteExecuteResponseData
    {
        public string result = "";
        public string errorCode = "";
        public bool needsResync;
        public int serverEpoch;
    }
}
