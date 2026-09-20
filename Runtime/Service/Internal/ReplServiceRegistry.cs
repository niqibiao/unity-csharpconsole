using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Zh1Zh1.CSharpConsole.Interface;

namespace Zh1Zh1.CSharpConsole.Service.Internal
{
    internal sealed class ReplServiceRegistry
    {
        private readonly ConcurrentDictionary<(string uuid, string path), IREPLExecutor> _executors = new ConcurrentDictionary<(string uuid, string path), IREPLExecutor>();
        private readonly ConcurrentDictionary<(string uuid, string path), IREPLCompiler> _compilers = new ConcurrentDictionary<(string uuid, string path), IREPLCompiler>();
        private readonly ConcurrentDictionary<string, double> _lastAccessTimes = new ConcurrentDictionary<string, double>();
        private readonly ConcurrentDictionary<string, string> _runtimeCompileSets = new ConcurrentDictionary<string, string>();
        private readonly ConcurrentDictionary<string, string> _runtimeBuildGuids = new ConcurrentDictionary<string, string>();
        private const double DEFAULT_IDLE_TIMEOUT_SECONDS = 21600.0; // 6 hours

        /// <summary>
        /// Keyed under a null path, which no runtime compile set has: a completion made
        /// before a runtime session's first submission lands here, and must not become the
        /// compiler that submission gets when it compiles against no set.
        /// </summary>
        public IREPLCompiler FetchEditorREPLCompiler(string uuid, Func<IREPLCompiler> generator)
        {
            var key = (uuid ?? "", (string)null);
            var compiler = _compilers.GetOrAdd(key, _ => generator.Invoke());
            TouchSession(uuid ?? "");
            return compiler;
        }

        public IREPLExecutor FetchExecutor(string uuid, Func<IREPLExecutor> generator)
        {
            var key = (uuid ?? "", "");
            var executor = _executors.GetOrAdd(key, _ => generator.Invoke());
            TouchSession(uuid ?? "");
            return executor;
        }

        public IREPLCompiler FetchRuntimeREPLCompiler(string uuid, string runtimeDllPath, Func<string, IREPLCompiler> generator)
        {
            var key = (uuid ?? "", runtimeDllPath ?? "");
            var compiler = _compilers.GetOrAdd(key, _ => generator.Invoke(runtimeDllPath));
            TouchSession(uuid ?? "");
            return compiler;
        }

        /// <summary>
        /// Records the compile set a runtime session's submissions are compiled against,
        /// which the editor resolves itself when the client names none -- so a completion
        /// for the session, which names none either, can use the same one. A session's
        /// submissions only chain within one set, so when the set changes, the compilers
        /// the session had under any other set -- or the editor compiler a completion made
        /// before its first submission -- are dropped rather than left holding their
        /// references.
        /// </summary>
        public void UseRuntimeCompileSet(string uuid, string compileSetPath, string registeredBuildGuid = null)
        {
            var sessionId = uuid ?? "";
            var buildChanged = registeredBuildGuid != null
                && _runtimeBuildGuids.TryGetValue(sessionId, out var previousBuildGuid)
                && !CompileSetStore.SameBuild(previousBuildGuid, registeredBuildGuid);
            if (registeredBuildGuid != null)
            {
                _runtimeBuildGuids[sessionId] = registeredBuildGuid;
            }
            else
            {
                _runtimeBuildGuids.TryRemove(sessionId, out _);
            }

            TouchSession(sessionId);
            var path = compileSetPath ?? "";
            if (!buildChanged && _runtimeCompileSets.TryGetValue(sessionId, out var current) && current == path)
            {
                return;
            }

            _runtimeCompileSets[sessionId] = path;
            foreach (var key in _compilers.Keys)
            {
                if (string.Equals(key.uuid, sessionId, StringComparison.Ordinal)
                    && (buildChanged || !string.Equals(key.path, path, StringComparison.Ordinal)))
                {
                    _compilers.TryRemove(key, out _);
                }
            }
        }

        /// <summary>
        /// Resolve a bound build's current decision for completion, including registrations
        /// made by another client. Explicit DLL paths keep their last-submission behavior.
        /// A forgotten decision must not fall back to Editor references.
        /// </summary>
        public string FindRuntimeCompileSet(string uuid)
        {
            var sessionId = uuid ?? "";
            if (_runtimeBuildGuids.TryGetValue(sessionId, out var buildGuid))
            {
                if (!CompileSetStore.TryLookup(buildGuid, out var setDirectory))
                {
                    throw new InvalidOperationException($"[REPL ALIGNMENT REQUIRED] No compile set decision remains for build {buildGuid}. Register its compile set or skip alignment before requesting completion.");
                }

                UseRuntimeCompileSet(sessionId, setDirectory, buildGuid);
            }

            return _runtimeCompileSets.TryGetValue(sessionId, out var path) ? path : null;
        }

        public bool RemoveEditorCompiler(string sessionId)
        {
            return _compilers.TryRemove((sessionId ?? "", null), out _);
        }

        public bool RemoveExecutor(string sessionId)
        {
            var key = (sessionId ?? "", "");
            return _executors.TryRemove(key, out _);
        }

        public bool HasCompilerForSession(string sessionId)
        {
            return _compilers.Keys.Any(key => string.Equals(key.uuid, sessionId, StringComparison.Ordinal));
        }

        public bool HasExecutorForSession(string sessionId)
        {
            var key = (sessionId ?? "", "");
            return _executors.ContainsKey(key);
        }

        public bool ResetSessionState(string sessionId)
        {
            var removedAny = _executors.TryRemove((sessionId ?? "", ""), out _);
            foreach (var key in _compilers.Keys)
            {
                if (string.Equals(key.uuid, sessionId, StringComparison.Ordinal)
                    && _compilers.TryRemove(key, out _))
                {
                    removedAny = true;
                }
            }

            _lastAccessTimes.TryRemove(sessionId, out _);
            _runtimeCompileSets.TryRemove(sessionId, out _);
            _runtimeBuildGuids.TryRemove(sessionId, out _);
            return removedAny;
        }

        public List<SessionStateInfo> ListSessions()
        {
            var states = new Dictionary<string, SessionStateInfo>(StringComparer.Ordinal);

            foreach (var key in _executors.Keys)
            {
                var sessionId = key.uuid;
                if (string.IsNullOrEmpty(sessionId))
                {
                    continue;
                }

                var state = GetOrCreateState(states, sessionId);
                state.hasExecutor = true;
            }

            foreach (var key in _compilers.Keys)
            {
                var sessionId = key.uuid;
                if (string.IsNullOrEmpty(sessionId))
                {
                    continue;
                }

                var state = GetOrCreateState(states, sessionId);
                state.hasCompiler = true;
            }

            return states.Values.OrderBy(state => state.sessionId, StringComparer.Ordinal).ToList();
        }

        public void RemoveCompilersForSession(string sessionId)
        {
            _runtimeCompileSets.TryRemove(sessionId ?? "", out _);
            _runtimeBuildGuids.TryRemove(sessionId ?? "", out _);
            foreach (var key in _compilers.Keys)
            {
                if (string.Equals(key.uuid, sessionId, StringComparison.Ordinal))
                {
                    _compilers.TryRemove(key, out _);
                }
            }
        }

        private static SessionStateInfo GetOrCreateState(Dictionary<string, SessionStateInfo> states, string sessionId)
        {
            if (!states.TryGetValue(sessionId, out var state))
            {
                state = new SessionStateInfo { sessionId = sessionId };
                states[sessionId] = state;
            }

            return state;
        }

        public void ClearAll()
        {
            _executors.Clear();
            _compilers.Clear();
            _lastAccessTimes.Clear();
            _runtimeCompileSets.Clear();
            _runtimeBuildGuids.Clear();
        }

        public int EvictIdleSessions(double idleTimeoutSeconds = DEFAULT_IDLE_TIMEOUT_SECONDS)
        {
            var now = ServiceTimestamp.Now();
            var evictedCount = 0;

            foreach (var kvp in _lastAccessTimes.ToArray())
            {
                var sessionId = kvp.Key;
                var lastAccess = kvp.Value;
                if ((now - lastAccess) < idleTimeoutSeconds)
                {
                    continue;
                }

                if (ResetSessionState(sessionId))
                {
                    evictedCount++;
                }
            }

            return evictedCount;
        }

        private void TouchSession(string sessionId)
        {
            if (!string.IsNullOrEmpty(sessionId))
            {
                _lastAccessTimes[sessionId] = ServiceTimestamp.Now();
            }
        }

    }
}
