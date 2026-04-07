using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Zh1Zh1.CSharpConsole.Interface;

namespace Zh1Zh1.CSharpConsole.Service.Internal
{
    internal sealed class ReplServiceRegistry
    {
        private readonly ConcurrentDictionary<(string uuid, string path), IREPLExecutor> _executors = new();
        private readonly ConcurrentDictionary<(string uuid, string path), IREPLCompiler> _compilers = new();
        private readonly ConcurrentDictionary<string, double> _lastAccessTimes = new();
        private const double DEFAULT_IDLE_TIMEOUT_SECONDS = 21600.0; // 6 hours

        public IREPLCompiler FetchEditorREPLCompiler(string uuid, Func<IREPLCompiler> generator)
        {
            var key = (uuid ?? "", "");
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

        public bool RemoveCompilerByKey((string uuid, string path) compilerKey)
        {
            return _compilers.TryRemove(compilerKey, out _);
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
