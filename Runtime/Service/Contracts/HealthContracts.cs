using System;

namespace Zh1Zh1.CSharpConsole.Service
{
    internal enum RefreshPhase
    {
        None,
        Requested,
        RefreshingAssets,
        Compiling,
        Reloading,
        Ready,
        Failed
    }

    [Serializable]
    internal class RefreshOperationState
    {
        public string opId = "";
        public string requestedAtUtc = "";
        public string action = "";
        public string phase = "";
        public bool compileRequested;
        public bool reloadObserved;
        public int generation;
        public int effectivePort;
        public string message = "";

        [NonSerialized]
        private RefreshPhase _phaseValue;

        public RefreshPhase PhaseValue
        {
            get => _phaseValue;
            set
            {
                _phaseValue = value;
                phase = ConsoleHttpService.PhaseToString(value);
            }
        }

        public void SyncPhaseFromSerialized()
        {
            _phaseValue = ConsoleHttpService.ParsePhase(phase);
            phase = ConsoleHttpService.PhaseToString(_phaseValue);
        }
    }

    [Serializable]
    internal class HealthResponse
    {
        public bool ok;
        public bool initialized;
        public bool isEditor;
        public int port;
        public bool refreshing;
        public int generation;
        public string editorState = "";
        public string packageVersion = "";
        public int protocolVersion;
        public string unityVersion = "";
        public bool isCompiling;
        public bool compileFailed;
        public RefreshOperationState operation = new();

#if !UNITY_EDITOR
        // Player-only field. Present in player /health responses, absent in editor
        // responses entirely (editor flow uses Mono Assembly.Load and the mode
        // distinction does not apply — sending an empty string would force clients
        // to write defensive "ignore when isEditor=true" checks).
        //
        // Values:
        //   "hybridCLR" — HybridCLR is integrated; player executes via Assembly.Load
        //   "lite"      — no HybridCLR; player executes via the Lite Roslyn ->
        //                 Expression interpreter path. LiteREPLExecutor ships with
        //                 the package, so a player without HybridCLR is always
        //                 Lite-capable.
        public string playerExecutorMode = "";
#endif
    }

    [Serializable]
    internal class RefreshRequest
    {
        public bool exitPlayModeIfNeeded;
        public string[] changedFiles;
    }

    [Serializable]
    internal class RefreshResponse
    {
        public bool ok;
        public bool accepted;
        public bool sessionsCleared;
        public bool refreshing;
        public bool exitPlayModeRequested;
        public int generation;
        public string message = "";
        public RefreshOperationState operation = new();
    }
}
