using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Zh1Zh1.CSharpConsole.Interface;

namespace Zh1Zh1.CSharpConsole.Service.Internal
{
    internal sealed class ReplServiceRegistry
    {
        private readonly ConcurrentDictionary<string, IREPLExecutor> _executors = new();
        private readonly ConcurrentDictionary<string, IREPLCompiler> _compilers = new();
        private readonly ConcurrentDictionary<string, string> _compilerSessionIds = new();

        public IREPLCompiler FetchEditorREPLCompiler(string uuid, Func<IREPLCompiler> generator)
        {
            var compiler = _compilers.GetOrAdd(uuid, _ => generator.Invoke());
            _compilerSessionIds[uuid] = uuid;
            return compiler;
        }

        public IREPLExecutor FetchExecutor(string uuid, Func<IREPLExecutor> generator)
        {
            return _executors.GetOrAdd(uuid, _ => generator.Invoke());
        }

        public IREPLCompiler FetchRuntimeREPLCompiler(string uuid, string runtimeDllPath, Func<string, IREPLCompiler> generator)
        {
            // Recreate the compiler when runtimeDllPath changes.
            var key = $"{uuid}_{runtimeDllPath ?? ""}";
            var compiler = _compilers.GetOrAdd(key, _ => generator.Invoke(runtimeDllPath));
            _compilerSessionIds[key] = uuid;
            return compiler;
        }

        public bool TryGetCompilerSessionId(string compilerKey, out string sessionId)
        {
            if (string.IsNullOrEmpty(compilerKey))
            {
                sessionId = string.Empty;
                return false;
            }

            if (_compilerSessionIds.TryGetValue(compilerKey, out sessionId) && !string.IsNullOrEmpty(sessionId))
            {
                return true;
            }

            // Fallback for older/in-memory keys when mapping has not been recorded yet.
            // Preserve the full key as session id to avoid ambiguous underscore parsing.
            sessionId = compilerKey;
            return true;
        }

        public bool RemoveCompilerByKey(string compilerKey)
        {
            if (_compilers.TryRemove(compilerKey, out _))
            {
                _compilerSessionIds.TryRemove(compilerKey, out _);
                return true;
            }

            _compilerSessionIds.TryRemove(compilerKey, out _);
            return false;
        }

        public bool RemoveExecutor(string sessionId)
        {
            return _executors.TryRemove(sessionId, out _);
        }

        public bool HasCompilerForSession(string sessionId)
        {
            return _compilers.Keys.Any(key => TryGetCompilerSessionId(key, out var keySessionId) && string.Equals(keySessionId, sessionId, StringComparison.Ordinal));
        }

        public bool HasExecutorForSession(string sessionId)
        {
            return _executors.ContainsKey(sessionId);
        }

        public bool ResetSessionState(string sessionId)
        {
            var removedAny = _executors.TryRemove(sessionId, out _);
            foreach (var key in _compilers.Keys)
            {
                if (TryGetCompilerSessionId(key, out var keySessionId)
                    && string.Equals(keySessionId, sessionId, StringComparison.Ordinal)
                    && RemoveCompilerByKey(key))
                {
                    removedAny = true;
                }
            }

            return removedAny;
        }

        public List<SessionStateInfo> ListSessions()
        {
            var states = new Dictionary<string, SessionStateInfo>(StringComparer.Ordinal);

            foreach (var sessionId in _executors.Keys)
            {
                if (string.IsNullOrEmpty(sessionId))
                {
                    continue;
                }

                var state = GetOrCreateState(states, sessionId);
                state.hasExecutor = true;
            }

            foreach (var key in _compilers.Keys)
            {
                if (!TryGetCompilerSessionId(key, out var sessionId) || string.IsNullOrEmpty(sessionId))
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
                if (TryGetCompilerSessionId(key, out var keySessionId)
                    && string.Equals(keySessionId, sessionId, StringComparison.Ordinal))
                {
                    RemoveCompilerByKey(key);
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
            _compilerSessionIds.Clear();
        }
    }
}
