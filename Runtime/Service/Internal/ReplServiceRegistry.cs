using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Zh1Zh1.CSharpConsole.Interface;
#if !CSHARPCONSOLE_LITE_DISABLED
using Zh1Zh1.CSharpConsole.Lite;
#endif

namespace Zh1Zh1.CSharpConsole.Service.Internal
{
    internal sealed class ReplServiceRegistry
    {
        private readonly ConcurrentDictionary<(string uuid, string path), IREPLExecutor> _executors = new();
#if !CSHARPCONSOLE_LITE_DISABLED
        private readonly ConcurrentDictionary<string, ILiteREPLExecutor> _liteExecutors = new();
#endif
        private readonly ConcurrentDictionary<(string uuid, string path), IREPLCompiler> _compilers = new();
#if !CSHARPCONSOLE_LITE_DISABLED
        // Lite compiler + type registry share session lifetime, key, and eviction
        // — bundling them prevents the "one slot dropped, the other lingered"
        // bug class and halves cleanup surface.
        private readonly ConcurrentDictionary<string, LiteEditorSession> _liteSessions = new();
#endif
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

#if !CSHARPCONSOLE_LITE_DISABLED
        public ILiteREPLExecutor FetchLiteExecutor(string uuid, Func<ILiteREPLExecutor> generator)
        {
            var key = uuid ?? "";
            var executor = _liteExecutors.GetOrAdd(key, _ => generator.Invoke());
            TouchSession(key);
            return executor;
        }

        public bool RemoveLiteExecutor(string sessionId)
        {
            return _liteExecutors.TryRemove(sessionId ?? "", out _);
        }

        public bool HasLiteExecutorForSession(string sessionId)
        {
            return _liteExecutors.ContainsKey(sessionId ?? "");
        }

        public LiteEditorSession FetchLiteSession(string uuid, Func<ILiteCompiler> compilerGenerator)
        {
            var key = uuid ?? "";
            var session = _liteSessions.GetOrAdd(key, _ => new LiteEditorSession(compilerGenerator.Invoke(), new SessionTypeRegistry()));
            TouchSession(key);
            return session;
        }

        public bool RemoveLiteSession(string sessionId)
        {
            return _liteSessions.TryRemove(sessionId ?? "", out _);
        }
#endif

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
#if !CSHARPCONSOLE_LITE_DISABLED
            if (_liteExecutors.TryRemove(sessionId ?? "", out _)) removedAny = true;
            if (_liteSessions.TryRemove(sessionId ?? "", out _)) removedAny = true;
#endif
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

#if !CSHARPCONSOLE_LITE_DISABLED
            foreach (var sessionId in _liteExecutors.Keys)
            {
                if (string.IsNullOrEmpty(sessionId)) continue;
                GetOrCreateState(states, sessionId).hasExecutor = true;
            }

            foreach (var sessionId in _liteSessions.Keys)
            {
                if (string.IsNullOrEmpty(sessionId)) continue;
                GetOrCreateState(states, sessionId).hasCompiler = true;
            }
#endif

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
#if !CSHARPCONSOLE_LITE_DISABLED
            _liteSessions.TryRemove(sessionId ?? "", out _);
#endif
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
#if !CSHARPCONSOLE_LITE_DISABLED
            _liteExecutors.Clear();
#endif
            _compilers.Clear();
#if !CSHARPCONSOLE_LITE_DISABLED
            _liteSessions.Clear();
#endif
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

#if !CSHARPCONSOLE_LITE_DISABLED
    internal sealed class LiteEditorSession
    {
        public ILiteCompiler Compiler { get; }
        public SessionTypeRegistry Registry { get; private set; }

        public LiteEditorSession(ILiteCompiler compiler, SessionTypeRegistry registry)
        {
            Compiler = compiler;
            Registry = registry;
        }

        // P1-2 auto-reset: when Player reports needsResync, drop all session
        // state on this side so it mirrors a freshly-restarted Player. Compiler
        // drops Roslyn chain + slot tables; Registry is replaced with a brand
        // new instance (epoch=0, empty tables) so the next submission's
        // envelope epoch matches the Player's freshly-reset epoch=0.
        public void ResetState()
        {
            Compiler.ResetSessionState();
            Registry = new SessionTypeRegistry();
        }
    }
#endif
}
