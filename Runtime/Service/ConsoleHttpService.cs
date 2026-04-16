using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zh1Zh1.CSharpConsole.Interface;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;
using Zh1Zh1.CSharpConsole.Service.Endpoints;
using Zh1Zh1.CSharpConsole.Service.Internal;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Compilation;
#endif
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Zh1Zh1.CSharpConsole.Service
{
    public static class ConsoleHttpService
    {
        public const int EDITOR_PORT = 14500;
        public const int PLAYER_PORT = 15500;

        private static Func<IREPLExecutor> s_EditorREPLExecutorGenerator;
        private static Func<IREPLCompiler> s_EditorREPLCompilerGenerator;
        private static Func<IREPLExecutor> s_RuntimeREPLExecutorGenerator;
        private static Func<string, IREPLCompiler> s_RuntimeREPLCompilerGenerator;

        private static HttpListener s_Listener;
        private static bool s_Initialized;
        private readonly static HttpClient s_HttpClient = new() { Timeout = TimeSpan.FromMilliseconds(ConsoleServiceConfig.HttpClientTimeoutMs) };
        private readonly static ReplServiceRegistry s_ReplServiceRegistry = new();
        private readonly static HttpEnvelopeFactory s_EnvelopeFactory = new();
        private static ConsoleHttpServiceDependencies s_Dependencies;
        private static HealthEndpointHandler s_HealthEndpointHandler;
        private static CommandEndpointHandler s_CommandEndpointHandler;
        private static BatchEndpointHandler s_BatchEndpointHandler;
#if UNITY_EDITOR
        private static CompletionEndpointHandler s_CompletionEndpointHandler;
#endif

        public static int Port { get; private set; }

        private static void BootstrapDependencies()
        {
            s_Dependencies ??= new ConsoleHttpServiceDependencies(
                s_EnvelopeFactory,
                BuildHealthResponseSnapshot,
                WriteEnvelopeResponseAsync,
                sessionId => s_ReplServiceRegistry.FetchEditorREPLCompiler(sessionId, s_EditorREPLCompilerGenerator),
                (sessionId, runtimeDllPath) => s_ReplServiceRegistry.FetchRuntimeREPLCompiler(sessionId, runtimeDllPath, s_RuntimeREPLCompilerGenerator));
            s_HealthEndpointHandler ??= new HealthEndpointHandler(s_Dependencies);
            s_CommandEndpointHandler ??= new CommandEndpointHandler(s_Dependencies);
            s_BatchEndpointHandler ??= new BatchEndpointHandler(s_Dependencies);
#if UNITY_EDITOR
            s_CompletionEndpointHandler ??= new CompletionEndpointHandler(s_Dependencies);
#endif
        }

        public static void InitializeForEditor( Func<IREPLCompiler> editorCompilerGenerator, Func<IREPLExecutor> editorExecutorGenerator, Func<string, IREPLCompiler> runtimeCompilerGenerator)
        {
#if UNITY_EDITOR
            MainThreadRequestRunner.InitializeEditor();
            s_EditorREPLCompilerGenerator = editorCompilerGenerator ?? throw new ArgumentNullException(nameof(editorCompilerGenerator));
            s_EditorREPLExecutorGenerator = editorExecutorGenerator ?? throw new ArgumentNullException(nameof(editorExecutorGenerator));
            s_RuntimeREPLCompilerGenerator = runtimeCompilerGenerator ?? throw new ArgumentNullException(nameof(runtimeCompilerGenerator));
            InitializeInternal();
#else
            throw new InvalidOperationException("InitializeForEditor can only be called in the Unity Editor.");
#endif
        }

        public static void InitializeForRuntime(Func<IREPLExecutor> runtimeExecutorGenerator)
        {
#if UNITY_EDITOR
            throw new InvalidOperationException("InitializeForRuntime can only be called in the Unity Runtime.");
#else
            MainThreadRequestRunner.InitializeRuntime();
            s_RuntimeREPLExecutorGenerator = runtimeExecutorGenerator ?? throw new ArgumentNullException(nameof(runtimeExecutorGenerator));
            InitializeInternal();
#endif
        }

        private static void InitializeInternal()
        {
            if (s_Initialized)
            {
                return;
            }

            var sw = Stopwatch.StartNew();

            BootstrapDependencies();
            StartListener();
            if (s_Listener?.IsListening != true)
            {
                // Listener failed — reset state so a future call can retry.
                s_Listener = null;
                Port = 0;
                ConsoleLog.Error("Service initialization failed: listener could not start");
                return;
            }

            s_Initialized = true;
#if UNITY_EDITOR
            var state = GetRefreshStateSnapshot();
            if (state.PhaseValue == RefreshPhase.Reloading || state.PhaseValue == RefreshPhase.Compiling || state.PhaseValue == RefreshPhase.RefreshingAssets || state.PhaseValue == RefreshPhase.Requested)
            {
                state.reloadObserved = true;
                SetPhase(state, RefreshPhase.Ready);
                state.message = "Service recovered after refresh";
            }
            // Always persist so that direct-launch discovery can read the port
            // even before any refresh cycle has run.
            SaveRefreshState(state);
#endif

            sw.Stop();
            ConsoleLog.Info($"Initialized service on port {Port}, elapsed={sw.ElapsedMilliseconds}ms");
        }

        public static void Shutdown()
        {
            if (!s_Initialized)
            {
                return;
            }

            s_Initialized = false;

            ClearSessionState();

            s_Listener?.Stop();
            s_Listener = null;
            Port = 0;

            ConsoleLog.Info("Service shutdown");
        }

        private static void StartListener()
        {
#if UNITY_EDITOR
            var defaultPort = EDITOR_PORT;
#else
            var defaultPort = PLAYER_PORT;
#endif
            if (TryStartHttpListener(defaultPort, 50000))
            {
                ThreadPool.QueueUserWorkItem(_ => _ = ListenForRequests());
            }
        }

        private static bool TryStartHttpListener(int minPort, int maxPort)
        {
            var currentTry = 0;
            const int maxTry = 10;

            Port = minPort;
            while (Port < maxPort)
            {
                try
                {
                    s_Listener = new HttpListener();
                    s_Listener.Prefixes.Add($"http://*:{Port}/CSharpConsole/");
                    s_Listener.Start();

                    ConsoleLog.Info($"HttpListener started on port {Port}");

                    return true;
                }
                catch (Exception ex) when (ex is HttpListenerException || ex is SocketException)
                {
                    s_Listener?.Close();
                    s_Listener = null;
                    Port++;
                    currentTry++;
                    if (currentTry > maxTry)
                    {
                        ConsoleLog.Error($"Failed to start HttpListener after {maxTry} attempts (ports {minPort}-{Port - 1}). {ex}");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    ConsoleLog.Error($"Failed to start HttpListener: {ex} {ex.Message}");
                    return false;
                }
            }

            return false;
        }

        private static async Task ListenForRequests()
        {
            var listener = s_Listener;
            if (listener == null)
            {
                return;
            }

            while (listener.IsListening)
            {
                HttpListenerContext context;

                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception e)
                {
                    ConsoleLog.Warning($"Listener exception: {e}");
                    continue;
                }

                if (context.Request.HttpMethod != "POST")
                {
                    context.Response.StatusCode = 405;
                    context.Response.Close();
                    continue;
                }

                var rawContentType = context.Request.ContentType;
                var contentType = rawContentType?.Split(';')[0].Trim().ToLowerInvariant();
                var path = context.Request.Url.AbsolutePath.ToLowerInvariant();

                await DispatchRequestByContentType(context, contentType, path);
            }
        }

        private static async Task DispatchRequestByContentType(HttpListenerContext context, string contentType, string path)
        {
            switch (contentType)
            {
                case "application/json":
                    if (await TryDispatchJsonRoute(context, path))
                    {
                        return;
                    }

                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    break;
                case "application/octet-stream":
                    if (await TryDispatchBinaryRoute(context, path))
                    {
                        return;
                    }

                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    break;
                default:
                    ConsoleLog.Warning($"Unsupported content-type: {contentType}");
                    context.Response.StatusCode = 415;
                    context.Response.Close();
                    break;
            }
        }

        private static async Task<bool> TryDispatchJsonRoute(HttpListenerContext context, string path)
        {
#if UNITY_EDITOR
            if (path.EndsWith("/editor"))
            {
                await ProcessEditorREPL(context);
                return true;
            }

            if (path.EndsWith("/compile"))
            {
                await ProcessCompileRuntimeREPL(context);
                return true;
            }

            if (path.EndsWith("/completion"))
            {
                await s_CompletionEndpointHandler.Handle(context);
                return true;
            }

            if (path.EndsWith("/editor-compile"))
            {
                await ProcessEditorCompileOnly(context);
                return true;
            }

            if (path.EndsWith("/runtime-compile"))
            {
                await ProcessRuntimeCompileOnly(context);
                return true;
            }

            if (path.EndsWith("/refresh"))
            {
                await ProcessRefresh(context);
                return true;
            }

#endif
            if (path.EndsWith("/command"))
            {
                await s_CommandEndpointHandler.Handle(context);
                return true;
            }
            if (path.EndsWith("/batch"))
            {
                await s_BatchEndpointHandler.Handle(context);
                return true;
            }
            if (path.EndsWith("/health"))
            {
                await s_HealthEndpointHandler.Handle(context);
                return true;
            }

            if (path.EndsWith("/execute"))
            {
                await ProcessExecuteRuntimeREPL(context);
                return true;
            }

            return false;
        }

        private static Task<bool> TryDispatchBinaryRoute(HttpListenerContext context, string path)
        {
#if UNITY_EDITOR
            if (path.EndsWith("/upload-dlls"))
            {
                return ProcessUploadDllsAndReturnTrue(context);
            }
#endif
            return Task.FromResult(false);
        }

#if UNITY_EDITOR
        private static async Task<bool> ProcessUploadDllsAndReturnTrue(HttpListenerContext context)
        {
            await ProcessUploadDlls(context);
            return true;
        }
#endif

        internal static List<SessionStateInfo> ListSessions()
        {
            return s_ReplServiceRegistry.ListSessions();
        }

        internal static bool HasCompilerForSession(string sessionId)
        {
            return s_ReplServiceRegistry.HasCompilerForSession(sessionId);
        }

        internal static bool HasExecutorForSession(string sessionId)
        {
            return s_ReplServiceRegistry.HasExecutorForSession(sessionId);
        }

        internal static bool ResetSessionState(string sessionId)
        {
            return s_ReplServiceRegistry.ResetSessionState(sessionId);
        }

        private static void ClearSessionState()
        {
            s_ReplServiceRegistry.ClearAll();
        }

#if UNITY_EDITOR
        private static async Task ProcessEditorREPL(HttpListenerContext context)
        {
            var message = await ConsoleHttpServiceDependencies.ReadRequestBodyAsync(context);
            HttpResponseEnvelope response;
            string uuid = null;
            try
            {
                var req = JsonUtility.FromJson<EditorREPLRequest>(message);
                var code = req.content;
                var defines = req.defines;
                var defaultUsing = req.defaultUsing;
                uuid = req.uuid;
                var reset = req.reset;
                ConsoleLog.Debug($"Editor request: codeLength={code.Length}, session={uuid}, reset={reset}");

                var result = await MainThreadRequestRunner.RunOnMainThreadAsync(async () =>
                {
                    if (reset)
                    {
                        s_ReplServiceRegistry.RemoveCompilerByKey((uuid, ""));
                        s_ReplServiceRegistry.RemoveExecutor(uuid);
                        return "REPL reset";
                    }

                    var compiler = s_ReplServiceRegistry.FetchEditorREPLCompiler(uuid, s_EditorREPLCompilerGenerator);
                    var executor = s_ReplServiceRegistry.FetchExecutor(uuid, s_EditorREPLExecutorGenerator);
                    var (assemblyBytes, scriptClassName, errorMsg) = compiler.Compile(code, defines, defaultUsing);

                    if (!string.IsNullOrEmpty(errorMsg))
                    {
                        return $"Compile failed:\n{errorMsg}";
                    }

                    if (assemblyBytes == null)
                    {
                        return string.Empty;
                    }

                    var evalResult = await executor.ExecuteAsync(assemblyBytes, scriptClassName);
                    return evalResult?.ToString() ?? string.Empty;
                });

                response = s_EnvelopeFactory.CreateTextEnvelope("execute", result, uuid);
            }
            catch (Exception e)
            {
                response = s_EnvelopeFactory.CreateTextEnvelope("execute", $"C# Exception: {e}", uuid);
            }

            await WriteEnvelopeResponseAsync(context, response, "Editor");
        }

        private static async Task ProcessEditorCompileOnly(HttpListenerContext context)
        {
            var message = await ConsoleHttpServiceDependencies.ReadRequestBodyAsync(context);
            var req = JsonUtility.FromJson<EditorREPLRequest>(message);
            var compiler = s_ReplServiceRegistry.FetchEditorREPLCompiler(req.uuid, s_EditorREPLCompilerGenerator);
            await CompileAndRespond(context, compiler, req.content, req.defines, req.defaultUsing);
        }

        private static async Task ProcessRuntimeCompileOnly(HttpListenerContext context)
        {
            var message = await ConsoleHttpServiceDependencies.ReadRequestBodyAsync(context);
            var req = JsonUtility.FromJson<CompileREPLRequest>(message);
            var compiler = s_ReplServiceRegistry.FetchRuntimeREPLCompiler(req.uuid, req.runtimeDllPath, s_RuntimeREPLCompilerGenerator);
            await CompileAndRespond(context, compiler, req.content, req.defines, req.defaultUsing);
        }

        private static async Task CompileAndRespond(HttpListenerContext context, IREPLCompiler compiler, string code, string defines, string defaultUsing)
        {
            CompileOnlyResponse responseData;
            try
            {
                var (assemblyBytes, scriptClassName, errorMsg) = compiler.Compile(code, defines, defaultUsing);
                responseData = new CompileOnlyResponse
                {
                    dllBase64 = assemblyBytes != null ? Convert.ToBase64String(assemblyBytes) : "",
                    className = scriptClassName ?? "",
                    error = errorMsg ?? ""
                };
            }
            catch (Exception e)
            {
                responseData = new CompileOnlyResponse { error = e.ToString() };
            }

            var ok = string.IsNullOrEmpty(responseData.error);
            var summary = ok ? "Compile succeeded" : responseData.error;
            var envelope = s_EnvelopeFactory.CreateEnvelope(ok, "compile", ok ? "ok" : "compile_error", summary, "", JsonUtility.ToJson(responseData));
            await WriteEnvelopeResponseAsync(context, envelope, "EditorCompile");
        }
#endif

        internal static HealthResponse BuildHealthResponseSnapshot()
        {
            s_ReplServiceRegistry.EvictIdleSessions();
            var state = GetRefreshStateSnapshot();
            return new HealthResponse
            {
                ok = true,
                initialized = s_Initialized,
#if UNITY_EDITOR
                isEditor = true,
#else
                isEditor = false,
#endif
                port = Port,
                refreshing = IsActiveRefreshPhase(state.PhaseValue),
                generation = Mathf.Max(0, state.generation),
                editorState = GetEditorState(state),
                packageVersion = ConsoleServiceConfig.PackageVersion,
                protocolVersion = ConsoleServiceConfig.ProtocolVersion,
                unityVersion = Application.unityVersion,
                operation = state
            };
        }

        private static async Task WriteJsonResponseAsync(HttpListenerContext context, string responseJson)
        {
            try
            {
                context.Response.ContentType = "application/json";
                var buffer = Encoding.UTF8.GetBytes(responseJson);
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            finally
            {
                context.Response.OutputStream.Close();
            }
        }

        private static async Task WriteEnvelopeResponseAsync(HttpListenerContext context, HttpResponseEnvelope response, string endpoint)
        {
            try
            {
                await WriteJsonResponseAsync(context, JsonUtility.ToJson(response));
            }
            catch (ObjectDisposedException)
            {
                ConsoleLog.Warning($"[{endpoint}] Response write skipped (client already disconnected)");
            }
            catch (IOException e)
            {
                ConsoleLog.Warning($"[{endpoint}] Response write failed (client disconnected): {e.Message}");
            }
            catch (Exception e)
            {
                ConsoleLog.Error($"[{endpoint}] Response write exception: {e}");
            }
        }

#if UNITY_EDITOR
        private const string REFRESH_ACTION = "refresh_and_compile";
        private const double REFRESH_GRACE_SECONDS = 2.0;
        private const double REFRESH_TRIGGER_TIMEOUT_SECONDS = 10.0;
        private static RefreshOperationState s_CachedRefreshState;
        private static long s_RefreshRequestedAtTicks;
        private static double s_RefreshTriggeredAtEditorTime;
        private static string[] s_PendingChangedFiles;

        private static string GetRefreshStatePath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Temp", "CSharpConsole", "refresh_state.json"));
        }

        private static RefreshOperationState LoadRefreshState()
        {
            try
            {
                var path = GetRefreshStatePath();
                if (!File.Exists(path))
                {
                    return null;
                }

                var json = File.ReadAllText(path);
                return string.IsNullOrWhiteSpace(json)
                    ? null
                    : NormalizeRefreshState(JsonUtility.FromJson<RefreshOperationState>(json));
            }
            catch (Exception e)
            {
                ConsoleLog.Debug($"Failed to read refresh state: {e}");
                return null;
            }
        }

        private static RefreshOperationState GetRefreshStateSnapshot()
        {
            if (s_CachedRefreshState != null)
            {
                return NormalizeRefreshState(s_CachedRefreshState);
            }

            return NormalizeRefreshState(LoadRefreshState() ?? new RefreshOperationState());
        }

        private static void SaveRefreshState(RefreshOperationState state)
        {
            try
            {
                state = NormalizeRefreshState(state);
                s_CachedRefreshState = state;
                var path = GetRefreshStatePath();
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(path, JsonUtility.ToJson(state));
            }
            catch (Exception e)
            {
                ConsoleLog.Warning($"Failed to write refresh state: {e}");
            }
        }

        private static RefreshOperationState NormalizeRefreshState(RefreshOperationState state)
        {
            state ??= new RefreshOperationState();
            state.SyncPhaseFromSerialized();
            state.effectivePort = Port;
            return state;
        }

        private static void SetPhase(RefreshOperationState state, RefreshPhase phase)
        {
            state.PhaseValue = phase;
        }

        private static bool IsActiveRefreshPhase(RefreshPhase phase)
        {
            return phase == RefreshPhase.Requested
                || phase == RefreshPhase.RefreshingAssets
                || phase == RefreshPhase.Compiling
                || phase == RefreshPhase.Reloading;
        }

        private static void UpdateRefreshState(Action<RefreshOperationState> update)
        {
            var state = GetRefreshStateSnapshot();
            update(state);
            SaveRefreshState(state);
        }

        private static int GetCurrentGeneration()
        {
            return Mathf.Max(0, GetRefreshStateSnapshot().generation);
        }

        private static void MarkRefreshFailed(string message)
        {
            UpdateRefreshState(state =>
            {
                SetPhase(state, RefreshPhase.Failed);
                state.message = message ?? "Refresh failed";
            });
        }

        private static void MarkRefreshReady(string message = null)
        {
            UpdateRefreshState(state =>
            {
                SetPhase(state, RefreshPhase.Ready);
                state.message = string.IsNullOrEmpty(message) ? "Refresh completed" : message;
            });
        }

        private static string GetEditorState(RefreshOperationState state)
        {
            if (!s_Initialized)
            {
                return "stopped";
            }

            state = NormalizeRefreshState(state);
            if (state.PhaseValue == RefreshPhase.None)
            {
                return PhaseToString(RefreshPhase.Ready);
            }

            if (state.PhaseValue == RefreshPhase.Failed)
            {
                return PhaseToString(RefreshPhase.Failed);
            }

            if (IsActiveRefreshPhase(state.PhaseValue))
            {
                return PhaseToString(state.PhaseValue);
            }

            return PhaseToString(RefreshPhase.Ready);
        }
#else
        private static RefreshOperationState GetRefreshStateSnapshot()
        {
            return new RefreshOperationState();
        }

        private static bool IsActiveRefreshPhase(RefreshPhase phase)
        {
            return false;
        }

        private static string GetEditorState(RefreshOperationState state)
        {
            return s_Initialized ? PhaseToString(RefreshPhase.Ready) : "stopped";
        }
#endif

#if UNITY_EDITOR
        public static void RegisterRefreshLifecycleCallbacks()
        {
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;
        }

        private static async Task ProcessRefresh(HttpListenerContext context)
        {
            RefreshResponse responseData;
            try
            {
                var body = await ConsoleHttpServiceDependencies.ReadRequestBodyAsync(context);
                var request = !string.IsNullOrWhiteSpace(body)
                    ? JsonUtility.FromJson<RefreshRequest>(body)
                    : null;

                var exitPlayModeRequested = false;
                if (request != null && request.exitPlayModeIfNeeded)
                {
                    MainThreadRequestRunner.Post(() =>
                    {
                        if (EditorApplication.isPlaying)
                        {
                            EditorApplication.isPlaying = false;
                        }
                    });
                    exitPlayModeRequested = true;
                }

                var current = GetRefreshStateSnapshot();
                if (IsActiveRefreshPhase(current.PhaseValue))
                {
                    responseData = new RefreshResponse
                    {
                        ok = true,
                        accepted = false,
                        sessionsCleared = false,
                        refreshing = true,
                        exitPlayModeRequested = exitPlayModeRequested,
                        generation = current.generation,
                        message = "Refresh already in progress",
                        operation = current
                    };
                }
                else
                {
                    var nextGeneration = Mathf.Max(0, current.generation) + 1;
                    var requestedAtUtc = DateTime.UtcNow.ToString("O");
                    var opId = Guid.NewGuid().ToString("N");
                    var nextState = new RefreshOperationState
                    {
                        opId = opId,
                        requestedAtUtc = requestedAtUtc,
                        action = REFRESH_ACTION,
                        compileRequested = true,
                        reloadObserved = false,
                        generation = nextGeneration,
                        message = "Refresh requested",
                        PhaseValue = RefreshPhase.Requested
                    };
                    SaveRefreshState(nextState);

                    ClearSessionState();

                    // Record request time (thread-safe) so OnEditorUpdate doesn't
                    // use a stale s_RefreshTriggeredAtEditorTime from a previous refresh.
                    s_RefreshRequestedAtTicks = DateTimeOffset.UtcNow.Ticks;

                    // Pass explicit file list to TriggerRefresh (if provided).
                    s_PendingChangedFiles = request?.changedFiles;

                    responseData = new RefreshResponse
                    {
                        ok = true,
                        accepted = true,
                        sessionsCleared = true,
                        refreshing = true,
                        exitPlayModeRequested = exitPlayModeRequested,
                        generation = nextState.generation,
                        message = "Refresh and script compilation scheduled. Existing compiler/executor sessions were cleared.",
                        operation = nextState
                    };

                    // Schedule on main thread via the thread-safe dispatcher.
                    // EditorApplication.delayCall is not reliable from HTTP threads.
                    MainThreadRequestRunner.Post(TriggerRefresh);
                }
            }
            catch (Exception e)
            {
                MarkRefreshFailed(e.ToString());
                responseData = new RefreshResponse
                {
                    ok = false,
                    accepted = false,
                    sessionsCleared = false,
                    refreshing = false,
                    generation = GetCurrentGeneration(),
                    message = e.ToString(),
                    operation = GetRefreshStateSnapshot()
                };
            }

            var ok = responseData.ok;
            var resultType = ok ? "ok" : "system_error";
            var summary = responseData.message ?? (ok ? "Refresh accepted" : "Refresh failed");
            var envelope = s_EnvelopeFactory.CreateEnvelope(ok, "bootstrap", resultType, summary, "", JsonUtility.ToJson(responseData));
            await WriteEnvelopeResponseAsync(context, envelope, "Refresh");
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(System.IntPtr hWnd);

        /// <summary>
        /// Bring the Unity Editor window to the foreground so the OS file watcher
        /// queue is flushed.  This makes AssetDatabase.Refresh() reliable even
        /// when Unity was running in the background.
        /// </summary>
        private static void ActivateEditorWindow()
        {
            try
            {
                var hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (hwnd != System.IntPtr.Zero)
                    SetForegroundWindow(hwnd);
            }
            catch { /* best-effort, non-Windows platforms ignore this */ }
        }

        private static void TriggerRefresh()
        {
            try
            {
                s_RefreshTriggeredAtEditorTime = EditorApplication.timeSinceStartup;

                // Consume the pending file list (set by ProcessRefresh on the HTTP thread).
                var explicitFiles = s_PendingChangedFiles;
                s_PendingChangedFiles = null;

                if (explicitFiles != null && explicitFiles.Length > 0)
                    TriggerRefreshTargeted(explicitFiles);
                else
                    TriggerRefreshFull();
            }
            catch (Exception e)
            {
                MarkRefreshFailed(e.ToString());
                ConsoleLog.Warning($"Refresh failed: {e}");
            }
        }

        /// <summary>
        /// Targeted refresh: caller provides exact file paths.
        /// Fast — no directory scanning, works for any path (Assets/, Packages/, etc.).
        /// </summary>
        private static void TriggerRefreshTargeted(string[] files)
        {
            UpdateRefreshState(state =>
            {
                SetPhase(state, RefreshPhase.RefreshingAssets);
                state.message = $"Importing {files.Length} file(s)";
            });

            var scriptCount = 0;
            var otherCount = 0;
            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (var path in files)
                {
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                    if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                        scriptCount++;
                    else
                        otherCount++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            if (scriptCount > 0)
                CompilationPipeline.RequestScriptCompilation();

            UpdateRefreshState(state =>
            {
                state.compileRequested = scriptCount > 0;
                if (EditorApplication.isCompiling)
                {
                    SetPhase(state, RefreshPhase.Compiling);
                    state.message = $"Compiling ({scriptCount} script(s), {otherCount} asset(s) updated)";
                }
                else
                {
                    SetPhase(state, RefreshPhase.RefreshingAssets);
                    state.message = scriptCount > 0
                        ? $"Waiting for compilation ({scriptCount} script(s), {otherCount} asset(s) updated)"
                        : $"{otherCount} non-script asset(s) refreshed";
                }
            });
        }

        /// <summary>
        /// Full refresh: no file list provided.
        /// Activates the editor window to flush file-watcher events, then
        /// lets AssetDatabase.Refresh() handle everything — detection, import,
        /// compilation, and domain reload are all managed by Unity.
        /// </summary>
        private static void TriggerRefreshFull()
        {
            UpdateRefreshState(state =>
            {
                SetPhase(state, RefreshPhase.RefreshingAssets);
                state.message = "Activating editor and refreshing assets";
            });

            // Bring editor to foreground so the OS file-watcher queue is flushed.
            // Without this, Refresh() misses external changes when Unity is in the background.
            ActivateEditorWindow();

            // Unity handles everything: detect changes, import, trigger compilation.
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            UpdateRefreshState(state =>
            {
                state.compileRequested = EditorApplication.isCompiling;
                if (EditorApplication.isCompiling)
                {
                    SetPhase(state, RefreshPhase.Compiling);
                    state.message = "Compiling after full asset refresh";
                }
                else
                {
                    SetPhase(state, RefreshPhase.RefreshingAssets);
                    state.message = "Asset refresh completed";
                }
            });
        }

        // ImportChangedScripts and timestamp persistence removed —
        // full-refresh mode now uses ActivateEditorWindow() + AssetDatabase.Refresh()
        // which lets Unity handle all detection, import, and compilation natively.
        // Targeted mode (changedFiles) uses ImportAsset directly in TriggerRefreshTargeted.

        private static void OnCompilationStarted(object _)
        {
            UpdateRefreshState(state =>
            {
                if (IsActiveRefreshPhase(state.PhaseValue))
                {
                    SetPhase(state, RefreshPhase.Compiling);
                    state.message = "Script compilation started";
                }
            });
        }

        private static void OnCompilationFinished(object _)
        {
            UpdateRefreshState(state =>
            {
                if (state.PhaseValue == RefreshPhase.Compiling)
                {
                    state.message = "Script compilation finished, waiting for reload or idle";
                }
            });

            EditorApplication.delayCall -= FinalizeRefreshAfterCompile;
            EditorApplication.delayCall += FinalizeRefreshAfterCompile;
        }

        private static void OnBeforeAssemblyReload()
        {
            // Stop listener before domain unload to prevent port leak and drift.
            Shutdown();

            UpdateRefreshState(state =>
            {
                if (IsActiveRefreshPhase(state.PhaseValue) || state.PhaseValue == RefreshPhase.Ready)
                {
                    SetPhase(state, RefreshPhase.Reloading);
                    state.message = "Assembly reload started";
                }
            });
        }

        private static void OnAfterAssemblyReload()
        {
            UpdateRefreshState(state =>
            {
                state.reloadObserved = true;
                SetPhase(state, RefreshPhase.Ready);
                state.message = "Assembly reload finished";
            });
        }

        private static void OnEditorUpdate()
        {
            var state = GetRefreshStateSnapshot();
            if (!IsActiveRefreshPhase(state.PhaseValue))
            {
                return;
            }

            if (EditorApplication.isCompiling)
            {
                if (state.PhaseValue != RefreshPhase.Compiling)
                {
                    UpdateRefreshState(s =>
                    {
                        SetPhase(s, RefreshPhase.Compiling);
                        s.message = "Script compilation in progress";
                    });
                }
                return;
            }

            if (EditorApplication.isUpdating)
            {
                return;
            }

            // Requested phase: TriggerRefresh hasn't run yet (scheduled via delayCall).
            // Wait for it — only apply a safety timeout to prevent infinite hang.
            if (state.PhaseValue == RefreshPhase.Requested)
            {
                var elapsedSec = (DateTimeOffset.UtcNow.Ticks - s_RefreshRequestedAtTicks) / (double)TimeSpan.TicksPerSecond;
                if (elapsedSec < REFRESH_TRIGGER_TIMEOUT_SECONDS)
                    return;
                MarkRefreshReady("Refresh trigger timed out");
                return;
            }

            if (state.PhaseValue == RefreshPhase.RefreshingAssets)
            {
                // No script changes — no compilation expected, done immediately.
                if (!state.compileRequested)
                {
                    MarkRefreshReady("Asset refresh completed without script compilation");
                    return;
                }

                // Scripts were imported and compilation was requested.
                // With ForceSynchronousImport, isCompiling usually becomes true
                // immediately; this grace period is a safety net.
                if (EditorApplication.timeSinceStartup - s_RefreshTriggeredAtEditorTime < REFRESH_GRACE_SECONDS)
                    return;

                MarkRefreshReady("Refresh completed without observable compilation work");
            }
        }

        private static void FinalizeRefreshAfterCompile()
        {
            var state = GetRefreshStateSnapshot();
            if (state.PhaseValue != RefreshPhase.Compiling)
            {
                return;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            MarkRefreshReady("Script compilation finished without assembly reload");
        }
#endif

        internal static string PhaseToString(RefreshPhase phase)
        {
            return phase switch
            {
                RefreshPhase.Requested => "requested",
                RefreshPhase.RefreshingAssets => "refreshing_assets",
                RefreshPhase.Compiling => "compiling",
                RefreshPhase.Reloading => "reloading",
                RefreshPhase.Ready => "ready",
                RefreshPhase.Failed => "failed",
                _ => ""
            };
        }

        internal static RefreshPhase ParsePhase(string phase)
        {
            return phase switch
            {
                "requested" => RefreshPhase.Requested,
                "refreshing_assets" => RefreshPhase.RefreshingAssets,
                "compiling" => RefreshPhase.Compiling,
                "reloading" => RefreshPhase.Reloading,
                "ready" => RefreshPhase.Ready,
                "failed" => RefreshPhase.Failed,
                _ => RefreshPhase.None
            };
        }

        private static async Task ProcessCompileRuntimeREPL(HttpListenerContext context)
        {
            var message = await ConsoleHttpServiceDependencies.ReadRequestBodyAsync(context);

            var result = "";
            string uuid = "";

            try
            {
                var req = JsonUtility.FromJson<CompileREPLRequest>(message);
                if (req == null)
                {
                    throw new InvalidOperationException("Compile request body is empty or invalid.");
                }

                var code = req.content ?? "";
                var defines = req.defines ?? "";
                var defaultUsing = req.defaultUsing ?? "";
                uuid = req.uuid ?? "";
                var targetIP = req.targetIP ?? "";
                var targetPort = req.targetPort ?? "";
                var runtimeDllPath = req.runtimeDllPath ?? "";
                var reset = req.reset;

                ConsoleLog.Debug($"Runtime compile request: codeLength={code.Length}, session={uuid}, target={targetIP}:{targetPort}, runtimeDllPath={runtimeDllPath}, reset={reset}");

                if (reset)
                {
                    if (string.IsNullOrEmpty(uuid))
                    {
                        throw new InvalidOperationException("Runtime reset requires a non-empty session id.");
                    }

                    s_ReplServiceRegistry.RemoveCompilersForSession(uuid);

                    result = await ForwardReset(targetIP, targetPort, uuid);
                }
                else
                {
                    var compiler = s_ReplServiceRegistry.FetchRuntimeREPLCompiler(uuid, runtimeDllPath, s_RuntimeREPLCompilerGenerator);
                    var (compileBytes, compileScriptClsName, errorMsg) = compiler.Compile(code, defines, defaultUsing);
                    if (!string.IsNullOrEmpty(errorMsg))
                    {
                        result = $"Compile failed: {errorMsg}";
                    }
                    else if (compileBytes == null)
                    {
                        result = "";
                    }
                    else
                    {
                        result = await ForwardDllToPlayer(targetIP, targetPort, uuid, compileBytes, compileScriptClsName);
                    }
                }
            }
            catch (Exception e)
            {
                result = $"Compile failed, {e}";
            }

            var envelope = s_EnvelopeFactory.CreateTextEnvelope("execute", result, uuid);
            await WriteEnvelopeResponseAsync(context, envelope, "RuntimeCompile");
        }

        private static string ParseExecuteResponseText(string responseText)
        {
            if (string.IsNullOrEmpty(responseText))
            {
                return string.Empty;
            }

            try
            {
                var envelope = JsonUtility.FromJson<HttpResponseEnvelope>(responseText);
                if (envelope != null && !string.IsNullOrEmpty(envelope.stage) && envelope.dataJson != null)
                {
                    var data = JsonUtility.FromJson<TextResponseData>(envelope.dataJson);
                    return data?.text ?? envelope.summary ?? string.Empty;
                }
            }
            catch (Exception e)
            {
                ConsoleLog.Warning($"Failed to parse execute response envelope JSON: {e}");
            }

            try
            {
                var response = JsonUtility.FromJson<ExecuteResponse>(responseText);
                if (response != null)
                {
                    if (!string.IsNullOrEmpty(response.error))
                    {
                        return response.error;
                    }

                    return response.result ?? string.Empty;
                }
            }
            catch (Exception e)
            {
                ConsoleLog.Warning($"Failed to parse execute response JSON: {e}");
            }

            return responseText;
        }

        private static async Task<string> ForwardDllToPlayer(string ip, string port, string uuid, byte[] dllBytes, string className)
        {
            var request = new ExecuteREPLRequest
            {
                dllBase64 = Convert.ToBase64String(dllBytes),
                className = className,
                uuid = uuid,
                reset = false
            };
            return await PostToPlayer(ip, port, request, "DLL");
        }

        private static async Task<string> ForwardReset(string ip, string port, string uuid)
        {
            var request = new ForwardResetRequest
            {
                uuid = uuid,
                reset = true
            };
            return await PostToPlayer(ip, port, request, "reset");
        }

        private static async Task<string> PostToPlayer<T>(string ip, string port, T request, string debugLabel)
        {
            try
            {
                var url = $"http://{ip}:{port}/CSharpConsole/execute";
                var jsonBytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(request));
                using var content = new ByteArrayContent(jsonBytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                using var response = await s_HttpClient.PostAsync(url, content);
                var responseText = await response.Content.ReadAsStringAsync();
                var executeText = ParseExecuteResponseText(responseText);
                if (!response.IsSuccessStatusCode)
                {
                    return ConsoleLog.Format($"Forward failed: {(int)response.StatusCode} {response.ReasonPhrase}: {executeText}");
                }

                ConsoleLog.Debug($"Forwarded {debugLabel} to {ip}:{port}, response={responseText}");
                return executeText;
            }
            catch (Exception ex)
            {
                return ConsoleLog.Format($"Forward failed: {ex}");
            }
        }

        private static string ResolveRuntimeDefinesPath(string extractDir)
        {
            if (string.IsNullOrEmpty(extractDir))
            {
                return "";
            }

            var runtimeDefinesPath = Path.Combine(extractDir, "runtime-defines.txt");
            return File.Exists(runtimeDefinesPath) ? runtimeDefinesPath : "";
        }

        private static async Task ProcessUploadDlls(HttpListenerContext context)
        {
            HttpResponseEnvelope response;
            try
            {
                using var ms = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(ms);
                var zipBytes = ms.ToArray();

                if (zipBytes.Length == 0)
                {
                    response = s_EnvelopeFactory.CreateEnvelope(false, "bootstrap", "validation_error", "empty request body", "", JsonUtility.ToJson(new UploadDllsResponse { error = "empty request body" }));
                    await WriteEnvelopeResponseAsync(context, response, "UploadDlls");
                    return;
                }

                ConsoleLog.Debug($"UploadDlls received {zipBytes.Length} bytes");

                using var sha = System.Security.Cryptography.SHA256.Create();
                var hashBytes = sha.ComputeHash(zipBytes);
                var contentHash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 16);

                var cacheRoot = Path.Combine(Path.GetTempPath(), "CSharpConsoleCache", "compileserver");
                var extractDir = Path.Combine(cacheRoot, contentHash);

                if (Directory.Exists(extractDir))
                {
                    ConsoleLog.Debug($"UploadDlls cache hit: {extractDir}");
                }
                else
                {
                    Directory.CreateDirectory(cacheRoot);
                    var tmpDir = extractDir + $".tmp.{System.Diagnostics.Process.GetCurrentProcess().Id}";
                    try
                    {
                        Directory.CreateDirectory(tmpDir);
                        using (var zipStream = new MemoryStream(zipBytes))
                        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
                        {
                            archive.ExtractToDirectory(tmpDir);
                        }

                        Directory.Move(tmpDir, extractDir);
                    }
                    catch
                    {
                        try { Directory.Delete(tmpDir, true); } catch { /* best effort */ }
                        throw;
                    }

                    ConsoleLog.Debug($"UploadDlls extracted to {extractDir}");
                }

                var runtimeDefinesPath = ResolveRuntimeDefinesPath(extractDir);
                var data = new UploadDllsResponse
                {
                    runtimeDllPath = extractDir,
                    runtimeDefinesPath = runtimeDefinesPath
                };

                ConsoleLog.Debug($"UploadDlls result: dllPath={extractDir}, runtimeDefinesPath={runtimeDefinesPath}");
                response = s_EnvelopeFactory.CreateEnvelope(true, "bootstrap", "ok", "Runtime DLL directory uploaded", "", JsonUtility.ToJson(data));
            }
            catch (Exception e)
            {
                ConsoleLog.Error($"UploadDlls exception: {e}");
                response = s_EnvelopeFactory.CreateEnvelope(false, "bootstrap", "system_error", e.Message, "", JsonUtility.ToJson(new UploadDllsResponse { error = e.Message }));
            }

            await WriteEnvelopeResponseAsync(context, response, "UploadDlls");
        }

        private static async Task ProcessExecuteRuntimeREPL(HttpListenerContext context)
        {
            var message = await ConsoleHttpServiceDependencies.ReadRequestBodyAsync(context);
            HttpResponseEnvelope response;
            string uuid = null;
            try
            {
                var req = JsonUtility.FromJson<ExecuteREPLRequest>(message);
                uuid = req.uuid;
                string result;
                if (req.reset)
                {
                    s_ReplServiceRegistry.RemoveExecutor(uuid);
                    result = "Reset Success!";
                }
                else
                {
                    var dllBase64 = req.dllBase64 ?? "";
                    var className = req.className ?? "";
                    ConsoleLog.Debug($"Execute request: dllLength={dllBase64.Length}, class={className}, session={uuid}, reset={req.reset}");
                    if (string.IsNullOrEmpty(dllBase64))
                    {
                        result = "No dll data";
                    }
                    else
                    {
                        result = await MainThreadRequestRunner.RunOnMainThreadAsync(async () =>
                        {
                            var dllBytes = Convert.FromBase64String(dllBase64);
                            var executor = s_ReplServiceRegistry.FetchExecutor(uuid, s_RuntimeREPLExecutorGenerator);
                            var execResult = await executor.ExecuteAsync(dllBytes, className);
                            return execResult?.ToString() ?? "";
                        });
                    }
                }

                response = s_EnvelopeFactory.CreateTextEnvelope("execute", result, uuid);
            }
            catch (Exception e)
            {
                response = s_EnvelopeFactory.CreateTextEnvelope("execute", ConsoleLog.Format($"Execute exception: {e}"), uuid);
            }

            await WriteEnvelopeResponseAsync(context, response, "Execute");
        }
    }
}
