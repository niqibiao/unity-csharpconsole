using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        private readonly static HttpClient s_HttpClient = new HttpClient() { Timeout = TimeSpan.FromMilliseconds(ConsoleServiceConfig.HttpClientTimeoutMs) };
        private readonly static ReplServiceRegistry s_ReplServiceRegistry = new ReplServiceRegistry();
        private readonly static HttpEnvelopeFactory s_EnvelopeFactory = new HttpEnvelopeFactory();
        private static InvocationCoordinator s_InvocationCoordinator;
        private static bool s_StartNewInvocationEpoch;
        private static ConsoleHttpServiceDependencies s_Dependencies;
        private static HealthEndpointHandler s_HealthEndpointHandler;
        private static CommandEndpointHandler s_CommandEndpointHandler;
        private static BatchEndpointHandler s_BatchEndpointHandler;
        private static long s_MainThreadHeartbeatUtcTicks;
        private static volatile bool s_CachedIsUpdating;
        private static volatile bool s_CachedIsPlaying;
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
            RecordMainThreadHeartbeat();
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
            Application.runInBackground = true;
            MainThreadRequestRunner.InitializeRuntime();
            RecordMainThreadHeartbeat();
            EnsureRuntimeHealthHeartbeat();
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

            if (s_InvocationCoordinator == null)
            {
                s_InvocationCoordinator = new InvocationCoordinator();
            }
            else if (s_StartNewInvocationEpoch)
            {
                s_InvocationCoordinator.RestartServiceEpoch();
            }
            s_StartNewInvocationEpoch = false;
            BootstrapDependencies();
            StartListener();
            if (s_Listener?.IsListening != true)
            {
                // Listener failed — reset state so a future call can retry.
                s_Listener = null;
                Port = 0;
                s_StartNewInvocationEpoch = true;
                ConsoleLog.Error("Service initialization failed: listener could not start");
                return;
            }

            s_Initialized = true;
#if UNITY_EDITOR
            var state = GetRefreshStateSnapshot();
            var resumablePlayModeExit =
                state.PhaseValue == RefreshPhase.Requested
                && state.triggerStarted
                && state.exitPlayModeRequested;
            if (resumablePlayModeExit)
            {
                TrackRefreshOperation(state);
                if (!EditorApplication.isPlaying
                    && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    state.waitingForEditMode = false;
                    state.message =
                        "Play Mode exited; waiting for Unity to become idle";
                }
            }
            else if (state.PhaseValue == RefreshPhase.Reloading && state.triggerStarted)
            {
                state.reloadObserved = true;
                SetPhase(state, RefreshPhase.Ready);
                state.message = "Service recovered after refresh";
            }
            else if (IsActiveRefreshPhase(state.PhaseValue))
            {
                SetPhase(state, RefreshPhase.Failed);
                state.message = state.triggerStarted
                    ? "Refresh was interrupted before completion could be confirmed"
                    : "Refresh was interrupted before its trigger started";
            }
            // Always persist so that direct-launch discovery can read the port
            // even before any refresh cycle has run.
            if (!TrySaveRefreshState(state, out var initializationPersistenceError))
            {
                RecordRefreshPersistenceFailure(
                    state,
                    $"Refresh state initialization could not be durably recorded: "
                    + initializationPersistenceError);
            }
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

            s_InvocationCoordinator?.MarkOutstandingOutcomeUnknown(
                "The Unity service stopped before the invocation outcome was durably recorded.");
            s_StartNewInvocationEpoch = true;
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

                try
                {
                    await DispatchRequestByContentType(context, contentType, path);
                }
                catch (Exception dispatchEx)
                {
                    ConsoleLog.Error($"Dispatch failed on path={path} contentType={contentType}: {dispatchEx}");
                    try
                    {
                        context.Response.StatusCode = 500;
                        context.Response.Close();
                    }
                    catch
                    {
                        // Client may have already disconnected; nothing more we can do.
                    }
                }
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
                await DispatchInvocationProtected(context, "editor", ProcessEditorREPL);
                return true;
            }

            if (path.EndsWith("/compile"))
            {
                await DispatchInvocationProtected(context, "compile", ProcessCompileRuntimeREPL);
                return true;
            }

            if (path.EndsWith("/completion"))
            {
                await s_CompletionEndpointHandler.Handle(context);
                return true;
            }

            if (path.EndsWith("/editor-compile"))
            {
                await DispatchInvocationProtected(context, "editor-compile", ProcessEditorCompileOnly);
                return true;
            }

            if (path.EndsWith("/runtime-compile"))
            {
                await DispatchInvocationProtected(context, "runtime-compile", ProcessRuntimeCompileOnly);
                return true;
            }

            if (path.EndsWith("/refresh"))
            {
                await DispatchInvocationProtected(context, "refresh", ProcessRefresh);
                return true;
            }

#endif
            if (path.EndsWith("/command"))
            {
                await DispatchInvocationProtected(context, "command", s_CommandEndpointHandler.Handle);
                return true;
            }
            if (path.EndsWith("/batch"))
            {
                await DispatchInvocationProtected(context, "batch", s_BatchEndpointHandler.Handle);
                return true;
            }
            if (path.EndsWith("/invocation-status"))
            {
                await ProcessInvocationStatus(context);
                return true;
            }
            if (path.EndsWith("/health"))
            {
                await s_HealthEndpointHandler.Handle(context);
                return true;
            }

            if (path.EndsWith("/execute"))
            {
                await DispatchInvocationProtected(context, "execute", ProcessExecuteRuntimeREPL);
                return true;
            }

            return false;
        }

        private static async Task DispatchInvocationProtected(
            HttpListenerContext context,
            string endpoint,
            Func<HttpListenerContext, Task> handler)
        {
            InvocationClaim claim = null;
            try
            {
                var cachedBody = await ConsoleHttpServiceDependencies.PrepareRequestBodyAsync(context);
                claim = s_InvocationCoordinator.Claim(
                    context.Request.Headers[InvocationCoordinator.InvocationIdHeader],
                    context.Request.Headers[InvocationCoordinator.TargetIdHeader],
                    endpoint,
                    cachedBody.Bytes);

                if (claim.Disposition == InvocationClaimDisposition.Execute
                    || claim.Disposition == InvocationClaimDisposition.Unprotected)
                {
                    ConsoleHttpServiceDependencies.AttachInvocationClaim(context, claim);
                    await handler(context);
                    return;
                }

                if (claim.Disposition == InvocationClaimDisposition.Replay)
                {
                    var replayEnvelope = string.IsNullOrWhiteSpace(claim.ResponseJson)
                        ? null
                        : JsonUtility.FromJson<HttpResponseEnvelope>(claim.ResponseJson);
                    if (replayEnvelope == null)
                    {
                        claim.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
                        s_InvocationCoordinator.MarkOutcomeUnknown(
                            claim,
                            "The persisted invocation response could not be decoded.");
                        replayEnvelope = CreateInvocationStateEnvelope(
                            claim,
                            InvocationClaimDisposition.OutcomeUnknown,
                            "The persisted invocation response could not be decoded.");
                    }
                    else
                    {
                        replayEnvelope.invocation =
                            s_InvocationCoordinator.CreateReceipt(claim, "replayed", true);
                    }

                    await WriteEnvelopeResponseAsync(context, replayEnvelope, endpoint);
                    return;
                }

                await WriteEnvelopeResponseAsync(
                    context,
                    CreateInvocationStateEnvelope(claim, claim.Disposition, claim.Message),
                    endpoint);
            }
            catch (Exception e)
            {
                ConsoleLog.Error($"[{endpoint}] Invocation dispatch failed: {e}");
                HttpResponseEnvelope envelope;
                if (claim != null
                    && claim.Disposition == InvocationClaimDisposition.Execute
                    && !string.IsNullOrEmpty(claim.InvocationId))
                {
                    claim.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
                    s_InvocationCoordinator.MarkOutcomeUnknown(
                        claim,
                        $"Invocation dispatch stopped unexpectedly: {e.Message}");
                    envelope = CreateInvocationStateEnvelope(
                        claim,
                        InvocationClaimDisposition.OutcomeUnknown,
                        "Invocation outcome is unknown; the request will not be dispatched again.");
                }
                else
                {
                    envelope = s_EnvelopeFactory.CreateEnvelope(
                        false,
                        "unknown",
                        "system_error",
                        $"Request dispatch failed: {e.Message}",
                        "",
                        "{}");
                    envelope.invocation = s_InvocationCoordinator.CreateReceipt(claim, "none", false);
                }

                await WriteEnvelopeResponseAsync(context, envelope, endpoint);
            }
            finally
            {
                ConsoleHttpServiceDependencies.ReleaseRequest(context);
            }
        }

        private static HttpResponseEnvelope CreateInvocationStateEnvelope(
            InvocationClaim claim,
            InvocationClaimDisposition disposition,
            string message)
        {
            var state = disposition switch
            {
                InvocationClaimDisposition.InProgress => "in_progress",
                InvocationClaimDisposition.Conflict => "conflict",
                InvocationClaimDisposition.OutcomeUnknown => "outcome_unknown",
                InvocationClaimDisposition.Rejected => "rejected",
                _ => "outcome_unknown"
            };
            var type = disposition switch
            {
                InvocationClaimDisposition.InProgress => "operation_in_progress",
                InvocationClaimDisposition.Conflict => "invocation_conflict",
                InvocationClaimDisposition.OutcomeUnknown => "outcome_unknown",
                InvocationClaimDisposition.Rejected => "validation_error",
                _ => "system_error"
            };
            var envelope = s_EnvelopeFactory.CreateEnvelope(
                false,
                "bootstrap",
                type,
                string.IsNullOrEmpty(message) ? "Invocation was not dispatched." : message,
                "",
                "{}");
            envelope.invocation = s_InvocationCoordinator.CreateReceipt(claim, state, false);
            return envelope;
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

        private static string ConsumeCompilerNotice(IREPLCompiler compiler)
        {
            return (compiler as IREPLCompilerNoticeProvider)?.ConsumeNotice() ?? "";
        }

        private static string CombineCompilerNotice(string notice, string result)
        {
            if (string.IsNullOrEmpty(notice))
            {
                return result ?? "";
            }

            if (string.IsNullOrEmpty(result))
            {
                return notice;
            }

            return $"{notice}\n\n{result}";
        }

        private sealed class RemoteInvocationOutcomeUnknownException : Exception
        {
            public RemoteInvocationOutcomeUnknownException(string message, Exception innerException = null)
                : base(message, innerException)
            {
            }
        }

        private static HttpResponseEnvelope CreateOutcomeUnknownEnvelope(
            string stage,
            Exception exception,
            string sessionId)
        {
            var summary = exception?.Message ?? "Main-thread execution outcome is unknown.";
            return s_EnvelopeFactory.CreateEnvelope(
                false,
                stage,
                "outcome_unknown",
                summary,
                sessionId,
                JsonUtility.ToJson(new TextResponseData { text = summary }));
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
                    var compilerNotice = ConsumeCompilerNotice(compiler);

                    if (!string.IsNullOrEmpty(errorMsg))
                    {
                        return $"Compile failed:\n{errorMsg}";
                    }

                    if (assemblyBytes == null)
                    {
                        return compilerNotice;
                    }

                    var evalResult = await executor.ExecuteAsync(assemblyBytes, scriptClassName);
                    return CombineCompilerNotice(compilerNotice, evalResult?.ToString());
                });

                response = s_EnvelopeFactory.CreateTextEnvelope("execute", result, uuid);
            }
            catch (MainThreadOutcomeUnknownException e)
            {
                response = CreateOutcomeUnknownEnvelope("execute", e, uuid);
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
            var compilerNotice = "";
            var failureType = "compile_error";
            try
            {
                var (assemblyBytes, scriptClassName, errorMsg) = compiler.Compile(code, defines, defaultUsing);
                compilerNotice = ConsumeCompilerNotice(compiler);
                responseData = new CompileOnlyResponse
                {
                    dllBase64 = assemblyBytes != null ? Convert.ToBase64String(assemblyBytes) : "",
                    className = scriptClassName ?? "",
                    error = errorMsg ?? ""
                };
            }
            catch (MainThreadOutcomeUnknownException e)
            {
                failureType = "outcome_unknown";
                responseData = new CompileOnlyResponse { error = e.Message };
            }
            catch (Exception e)
            {
                responseData = new CompileOnlyResponse { error = e.ToString() };
            }

            var ok = string.IsNullOrEmpty(responseData.error);
            var summary = ok
                ? (string.IsNullOrEmpty(compilerNotice) ? "Compile succeeded" : compilerNotice)
                : responseData.error;
            var envelope = s_EnvelopeFactory.CreateEnvelope(ok, "compile", ok ? "ok" : failureType, summary, "", JsonUtility.ToJson(responseData));
            await WriteEnvelopeResponseAsync(context, envelope, "EditorCompile");
        }
#endif

        private static RefreshOperationState CreatePublicRefreshState(
            RefreshOperationState state)
        {
#if UNITY_EDITOR
            var publicState = CloneRefreshState(state);
#else
            var publicState = state ?? new RefreshOperationState();
#endif
            publicState.changedFileCount =
                state?.changedFiles?.Length
                ?? state?.changedFileCount
                ?? 0;
            publicState.changedFiles = Array.Empty<string>();
            return publicState;
        }

        internal static HealthResponse BuildHealthResponseSnapshot()
        {
            s_ReplServiceRegistry.EvictIdleSessions();
            s_InvocationCoordinator.Maintain();
            var state = GetRefreshStateSnapshot();
            return new HealthResponse
            {
                ok = true,
                initialized = s_Initialized,
#if UNITY_EDITOR
                isEditor = true,
                isCompiling = s_CachedIsCompiling,
                compileFailed = s_CachedCompileFailed,
#else
                isEditor = false,
                isCompiling = false,
                compileFailed = false,
#endif
                port = Port,
                refreshing = IsActiveRefreshPhase(state.PhaseValue),
                generation = Mathf.Max(0, state.generation),
                editorState = GetEditorState(state),
                packageVersion = ConsoleServiceConfig.PackageVersion,
                protocolVersion = ConsoleServiceConfig.ProtocolVersion,
                unityVersion = Application.unityVersion,
                targetId = s_InvocationCoordinator.TargetId,
                serviceEpoch = s_InvocationCoordinator.ServiceEpoch,
                capabilities = new[]
                {
                    "invocation_headers",
                    "invocation_receipts",
                    "invocation_status",
                    "at_most_once",
#if UNITY_EDITOR
                    "test_runs_v1"
#endif
                },
                journalWritable = s_InvocationCoordinator.JournalWritable,
                dedupeWindowSeconds = InvocationCoordinator.DedupeWindowSeconds,
                isUpdating = s_CachedIsUpdating,
                isPlaying = s_CachedIsPlaying,
                mainThreadHeartbeatAgeMs = GetMainThreadHeartbeatAgeMs(),
                operation = CreatePublicRefreshState(state)
            };
        }

        private static async Task ProcessInvocationStatus(HttpListenerContext context)
        {
            HttpResponseEnvelope envelope;
            try
            {
                var body = await ConsoleHttpServiceDependencies.ReadRequestBodyAsync(context);
                var request = string.IsNullOrWhiteSpace(body)
                    ? null
                    : JsonUtility.FromJson<InvocationStatusRequest>(body);
                if (request == null)
                {
                    envelope = s_EnvelopeFactory.CreateEnvelope(
                        false,
                        "bootstrap",
                        "validation_error",
                        "Invocation status request body is empty or invalid.",
                        "",
                        "{}");
                }
                else
                {
                    var headerInvocationId =
                        context.Request.Headers[InvocationCoordinator.InvocationIdHeader]?.Trim() ?? "";
                    var headerTargetId =
                        context.Request.Headers[InvocationCoordinator.TargetIdHeader]?.Trim() ?? "";
                    var invocationId = request.invocationId?.Trim() ?? "";
                    var targetId = request.targetId?.Trim() ?? "";

                    if (!string.IsNullOrEmpty(headerInvocationId)
                        && !string.Equals(headerInvocationId, invocationId, StringComparison.OrdinalIgnoreCase))
                    {
                        envelope = s_EnvelopeFactory.CreateEnvelope(
                            false,
                            "bootstrap",
                            "validation_error",
                            "Invocation id header does not match the status request body.",
                            "",
                            "{}");
                    }
                    else if (!string.IsNullOrEmpty(headerTargetId)
                        && !string.IsNullOrEmpty(targetId)
                        && !string.Equals(headerTargetId, targetId, StringComparison.Ordinal))
                    {
                        envelope = s_EnvelopeFactory.CreateEnvelope(
                            false,
                            "bootstrap",
                            "validation_error",
                            "Target id header does not match the status request body.",
                            "",
                            "{}");
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(targetId))
                        {
                            targetId = headerTargetId;
                        }

                        if (s_InvocationCoordinator.TryGetStatus(
                            invocationId,
                            targetId,
                            out var status,
                            out var error))
                        {
                            envelope = s_EnvelopeFactory.CreateEnvelope(
                                true,
                                "bootstrap",
                                "ok",
                                status.found
                                    ? $"Invocation is {status.state}."
                                    : "Invocation was not found in the active dedupe window.",
                                "",
                                JsonUtility.ToJson(status));
                        }
                        else
                        {
                            envelope = s_EnvelopeFactory.CreateEnvelope(
                                false,
                                "bootstrap",
                                "validation_error",
                                error,
                                "",
                                "{}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                envelope = s_EnvelopeFactory.CreateEnvelope(
                    false,
                    "bootstrap",
                    "system_error",
                    $"Invocation status failed: {e.Message}",
                    "",
                    "{}");
            }

            await WriteEnvelopeResponseAsync(context, envelope, "InvocationStatus");
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
            var claim = ConsoleHttpServiceDependencies.GetInvocationClaim(context);
            var invocationTerminalRecorded = false;
            try
            {
                response ??= s_EnvelopeFactory.CreateEnvelope(
                    false,
                    "unknown",
                    "system_error",
                    "Response envelope was empty.",
                    "",
                    "{}");
                string responseJson;

                if (claim != null && !string.IsNullOrEmpty(claim.InvocationId))
                {
                    claim.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
                    if (string.Equals(response.type, "outcome_unknown", StringComparison.Ordinal))
                    {
                        response.invocation = s_InvocationCoordinator.MarkOutcomeUnknown(
                            claim,
                            string.IsNullOrEmpty(response.summary)
                                ? "Invocation outcome is unknown."
                                : response.summary);
                        invocationTerminalRecorded = true;
                        responseJson = JsonUtility.ToJson(response);
                    }
                    else
                    {
                        response.invocation =
                            s_InvocationCoordinator.CreateReceipt(claim, "completed", false);
                        responseJson = JsonUtility.ToJson(response);
                        if (s_InvocationCoordinator.TryComplete(claim, responseJson, out var persistenceError))
                        {
                            invocationTerminalRecorded = true;
                        }
                        else
                        {
                            s_InvocationCoordinator.MarkOutcomeUnknown(claim, persistenceError);
                            invocationTerminalRecorded = true;
                            response = s_EnvelopeFactory.CreateEnvelope(
                                false,
                                "unknown",
                                "outcome_unknown",
                                string.IsNullOrEmpty(persistenceError)
                                    ? "Invocation result could not be durably recorded."
                                    : persistenceError,
                                response.sessionId,
                                "{}");
                            response.invocation =
                                s_InvocationCoordinator.CreateReceipt(claim, "outcome_unknown", false);
                            responseJson = JsonUtility.ToJson(response);
                        }
                    }
                }
                else
                {
                    if (claim != null)
                    {
                        response.invocation =
                            s_InvocationCoordinator.CreateReceipt(claim, "none", false);
                    }

                    responseJson = JsonUtility.ToJson(response);
                }

                await WriteJsonResponseAsync(context, responseJson);
            }
            catch (ObjectDisposedException)
            {
                RecordUnknownInvocationIfNeeded(
                    claim,
                    ref invocationTerminalRecorded,
                    "Response handling stopped before a terminal invocation record was persisted.");
                ConsoleLog.Warning($"[{endpoint}] Response write skipped (client already disconnected)");
            }
            catch (IOException e)
            {
                RecordUnknownInvocationIfNeeded(
                    claim,
                    ref invocationTerminalRecorded,
                    $"Response handling failed before a terminal invocation record was persisted: {e.Message}");
                ConsoleLog.Warning($"[{endpoint}] Response write failed (client disconnected): {e.Message}");
            }
            catch (Exception e)
            {
                RecordUnknownInvocationIfNeeded(
                    claim,
                    ref invocationTerminalRecorded,
                    $"Response persistence failed: {e.Message}");
                ConsoleLog.Error($"[{endpoint}] Response write exception: {e}");
            }
            finally
            {
                ConsoleHttpServiceDependencies.ReleaseRequest(context);
            }
        }

        private static void RecordUnknownInvocationIfNeeded(
            InvocationClaim claim,
            ref bool invocationTerminalRecorded,
            string message)
        {
            if (invocationTerminalRecorded
                || claim == null
                || string.IsNullOrEmpty(claim.InvocationId))
            {
                return;
            }

            s_InvocationCoordinator.MarkOutcomeUnknown(claim, message);
            invocationTerminalRecorded = true;
        }

        private static void RecordMainThreadHeartbeat()
        {
            Interlocked.Exchange(ref s_MainThreadHeartbeatUtcTicks, DateTime.UtcNow.Ticks);
#if UNITY_EDITOR
            s_CachedIsUpdating = EditorApplication.isUpdating;
            s_CachedIsPlaying = EditorApplication.isPlaying;
#else
            s_CachedIsUpdating = false;
            s_CachedIsPlaying = Application.isPlaying;
#endif
        }

        private static int GetMainThreadHeartbeatAgeMs()
        {
            var lastTicks = Interlocked.Read(ref s_MainThreadHeartbeatUtcTicks);
            if (lastTicks <= 0)
            {
                return -1;
            }

            var elapsedTicks = Math.Max(0, DateTime.UtcNow.Ticks - lastTicks);
            var elapsedMs = elapsedTicks / TimeSpan.TicksPerMillisecond;
            return elapsedMs > int.MaxValue ? int.MaxValue : (int)elapsedMs;
        }

#if !UNITY_EDITOR
        private sealed class RuntimeHealthHeartbeat : MonoBehaviour
        {
            private void Update()
            {
                RecordMainThreadHeartbeat();
            }
        }

        private static void EnsureRuntimeHealthHeartbeat()
        {
            var heartbeatObject = new GameObject("CSharpConsoleHealthHeartbeat")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            UnityEngine.Object.DontDestroyOnLoad(heartbeatObject);
            heartbeatObject.AddComponent<RuntimeHealthHeartbeat>();
        }
#endif

#if UNITY_EDITOR
        private const string REFRESH_ACTION = "refresh_and_compile";
        private const string REFRESH_STATE_ERROR_ACTION = "refresh_state_unreadable";
        private const double REFRESH_COMPILE_START_TIMEOUT_SECONDS = 30.0;
        private const double REFRESH_COMPILE_FINISH_TIMEOUT_SECONDS = 300.0;
        private const double REFRESH_RELOAD_START_TIMEOUT_SECONDS = 60.0;
        private const double REFRESH_POST_COMPILE_QUIET_SECONDS = 2.0;
        private const int REFRESH_POST_COMPILE_QUIET_UPDATES = 3;
        private const double REFRESH_TRIGGER_TIMEOUT_SECONDS = 10.0;
        private const double REFRESH_EXIT_PLAYMODE_TIMEOUT_SECONDS = 120.0;
        private static readonly object s_RefreshStateGate = new object();
        private static RefreshOperationState s_CachedRefreshState;
        private static long s_RefreshRequestedAtTicks;
        private static double s_CompileRequestedAtEditorTime;
        private static double s_CompileStartedAtEditorTime;
        private static double s_CompilationFinishedAtEditorTime;
        private static bool s_CompilationStartedObserved;
        private static bool s_AssemblyCompilationStartedObserved;
        private static bool s_CompilationFinishedObserved;
        private static int s_PostCompileQuietUpdateCount;
        private static string s_TrackedRefreshOperationId = "";
        private static int s_TrackedRefreshGeneration;

        // Cached on the main thread so /health (background HTTP thread) can read
        // them safely. UnityEditor.EditorUtility.scriptCompilationFailed throws
        // UnityException when accessed off-main-thread on Unity 2022.3.
        private static volatile bool s_CachedIsCompiling;
        private static volatile bool s_CachedCompileFailed;

        private static void RefreshCompilationFlagCache()
        {
            RecordMainThreadHeartbeat();
            s_CachedIsCompiling = EditorApplication.isCompiling;
            s_CachedCompileFailed = EditorUtility.scriptCompilationFailed;
        }

        private static string GetRefreshStatePath()
        {
            return Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "..",
                "Library",
                "CSharpConsole",
                "RefreshState",
                "v1",
                "refresh_state.json"));
        }

        private static string GetLegacyRefreshStatePath()
        {
            return Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "..",
                "Temp",
                "CSharpConsole",
                "refresh_state.json"));
        }

        private static RefreshOperationState LoadRefreshState()
        {
            try
            {
                var path = GetRefreshStatePath();
                var loadingLegacyState = false;
                RecoverRefreshStateFile(path);
                if (!File.Exists(path))
                {
                    // Read the previous Temp location once for compatibility.
                    // Initialization persists any valid legacy state into the
                    // Library-backed canonical path before advertising ready.
                    path = GetLegacyRefreshStatePath();
                    RecoverRefreshStateFile(path);
                    if (!File.Exists(path))
                    {
                        return null;
                    }
                    loadingLegacyState = true;
                }

                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return CreateUnreadableRefreshState(
                        "Refresh state file is empty; the previous refresh outcome is unknown");
                }

                var state = JsonUtility.FromJson<RefreshOperationState>(json);
                if (state == null)
                {
                    return CreateUnreadableRefreshState(
                        "Refresh state file could not be decoded; the previous refresh outcome is unknown");
                }

                string validationError;
                var valid = loadingLegacyState
                    ? TryMigrateLegacyRefreshStateDocument(
                        json,
                        state,
                        out validationError)
                    : TryValidateRefreshStateDocument(
                        json,
                        state,
                        out validationError);
                if (!valid)
                {
                    return CreateUnreadableRefreshState(
                        "Refresh state file is structurally invalid; the previous "
                        + $"refresh outcome is unknown: {validationError}");
                }

                return NormalizeRefreshState(state);
            }
            catch (Exception e)
            {
                ConsoleLog.Warning($"Failed to read refresh state: {e}");
                return CreateUnreadableRefreshState(
                    $"Refresh state could not be read; the previous refresh outcome is unknown: {e.Message}");
            }
        }

        private static bool TryMigrateLegacyRefreshStateDocument(
            string json,
            RefreshOperationState state,
            out string error)
        {
            error = "";
            var legacyFields = new[]
            {
                "opId",
                "requestedAtUtc",
                "action",
                "phase",
                "compileRequested",
                "reloadObserved",
                "generation",
                "effectivePort",
                "message"
            };
            foreach (var field in legacyFields)
            {
                if (!ContainsJsonProperty(json, field))
                {
                    error = $"legacy required field '{field}' is missing";
                    return false;
                }
            }

            state.triggerStarted =
                !string.IsNullOrEmpty(state.opId)
                && state.generation > 0;
            state.exitPlayModeRequested = false;
            state.waitingForEditMode = false;
            state.changedFiles = Array.Empty<string>();
            state.changedFileCount = 0;
            state.SyncPhaseFromSerialized();
            if (string.IsNullOrEmpty(state.opId)
                && state.generation == 0
                && state.PhaseValue == RefreshPhase.Ready)
            {
                // Older callbacks could persist a synthetic ready state even
                // when no refresh operation had ever existed. Normalize that
                // discovery-only record to the current pristine shape.
                state.requestedAtUtc = "";
                state.action = "";
                state.triggerStarted = false;
                state.compileRequested = false;
                state.reloadObserved = false;
                state.exitPlayModeRequested = false;
                state.waitingForEditMode = false;
                state.changedFiles = Array.Empty<string>();
                state.changedFileCount = 0;
                state.message = "";
                SetPhase(state, RefreshPhase.None);
            }
            if (IsActiveRefreshPhase(state.PhaseValue))
            {
                SetPhase(state, RefreshPhase.Failed);
                state.message =
                    "Legacy refresh was active during the reliability upgrade; "
                    + "its completion could not be confirmed";
            }

            return TryValidateRefreshStateDocument(
                JsonUtility.ToJson(state),
                state,
                out error);
        }

        private static RefreshOperationState CreateUnreadableRefreshState(string message)
        {
            return new RefreshOperationState
            {
                PhaseValue = RefreshPhase.Failed,
                action = REFRESH_STATE_ERROR_ACTION,
                effectivePort = Port,
                message = message ?? "Refresh state is unreadable"
            };
        }

        private static bool TryValidateRefreshStateDocument(
            string json,
            RefreshOperationState state,
            out string error)
        {
            error = "";
            var requiredFields = new[]
            {
                "opId",
                "requestedAtUtc",
                "action",
                "phase",
                "triggerStarted",
                "compileRequested",
                "reloadObserved",
                "exitPlayModeRequested",
                "waitingForEditMode",
                "changedFiles",
                "changedFileCount",
                "generation",
                "effectivePort",
                "message"
            };
            foreach (var field in requiredFields)
            {
                if (!ContainsJsonProperty(json, field))
                {
                    error = $"required field '{field}' is missing";
                    return false;
                }
            }

            var serializedPhase = state.phase ?? "";
            var pristine = string.IsNullOrEmpty(state.opId)
                && string.IsNullOrEmpty(state.requestedAtUtc)
                && string.IsNullOrEmpty(state.action)
                && string.IsNullOrEmpty(serializedPhase)
                && !state.triggerStarted
                && !state.compileRequested
                && !state.reloadObserved
                && !state.exitPlayModeRequested
                && !state.waitingForEditMode
                && (state.changedFiles == null || state.changedFiles.Length == 0)
                && state.changedFileCount == 0
                && state.generation == 0
                && string.IsNullOrEmpty(state.message);
            if (pristine)
            {
                return true;
            }

            var unreadableMarker =
                string.Equals(
                    state.action,
                    REFRESH_STATE_ERROR_ACTION,
                    StringComparison.Ordinal)
                && string.Equals(serializedPhase, "failed", StringComparison.Ordinal)
                && string.IsNullOrEmpty(state.opId)
                && string.IsNullOrEmpty(state.requestedAtUtc)
                && state.generation == 0
                && !state.triggerStarted
                && !state.compileRequested
                && !state.reloadObserved
                && !state.exitPlayModeRequested
                && !state.waitingForEditMode
                && (state.changedFiles == null || state.changedFiles.Length == 0)
                && state.changedFileCount == 0
                && !string.IsNullOrEmpty(state.message);
            if (unreadableMarker)
            {
                return true;
            }

            if (!Guid.TryParseExact(state.opId, "N", out _))
            {
                error = "opId is missing or is not a 32-character UUID";
                return false;
            }
            if (state.generation <= 0)
            {
                error = "generation must be positive for an operation record";
                return false;
            }
            if (state.changedFileCount < 0
                || state.changedFileCount
                    != (state.changedFiles?.Length ?? 0))
            {
                error = "changedFileCount does not match changedFiles";
                return false;
            }
            if (!string.Equals(state.action, REFRESH_ACTION, StringComparison.Ordinal))
            {
                error = $"action must be '{REFRESH_ACTION}'";
                return false;
            }
            if (!DateTimeOffset.TryParseExact(
                    state.requestedAtUtc,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out _))
            {
                error = "requestedAtUtc is missing or is not an ISO-8601 round-trip timestamp";
                return false;
            }

            var phase = ParsePhase(serializedPhase);
            if (phase == RefreshPhase.None)
            {
                error = $"phase '{serializedPhase}' is not recognized";
                return false;
            }
            if (phase == RefreshPhase.Requested
                && state.triggerStarted
                && !state.exitPlayModeRequested)
            {
                error = "a started requested phase is only valid while exiting Play Mode";
                return false;
            }
            if ((phase == RefreshPhase.RefreshingAssets
                    || phase == RefreshPhase.Compiling
                    || phase == RefreshPhase.Reloading)
                && !state.triggerStarted)
            {
                error = $"phase '{serializedPhase}' requires triggerStarted=true";
                return false;
            }
            if (state.waitingForEditMode
                && (
                    phase != RefreshPhase.Requested
                    || !state.triggerStarted
                    || !state.exitPlayModeRequested
                ))
            {
                error = "waitingForEditMode is inconsistent with the requested phase";
                return false;
            }
            if (state.reloadObserved
                && (
                    phase != RefreshPhase.Ready
                    || !state.triggerStarted
                ))
            {
                error = "reloadObserved is only valid for a completed started operation";
                return false;
            }

            return true;
        }

        private static bool ContainsJsonProperty(string json, string propertyName)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName))
            {
                return false;
            }

            var marker = $"\"{propertyName}\"";
            var index = 0;
            while ((index = json.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
            {
                var cursor = index + marker.Length;
                while (cursor < json.Length && char.IsWhiteSpace(json[cursor]))
                {
                    cursor++;
                }
                if (cursor < json.Length && json[cursor] == ':')
                {
                    return true;
                }
                index = cursor;
            }

            return false;
        }

        private static void RecoverRefreshStateFile(string path)
        {
            var backupPath = path + ".backup";
            var directory = Path.GetDirectoryName(path);
            if (!File.Exists(path) && File.Exists(backupPath))
            {
                File.Move(backupPath, path);
            }

            if (File.Exists(path) && File.Exists(backupPath))
            {
                try
                {
                    File.Delete(backupPath);
                }
                catch (Exception e)
                {
                    ConsoleLog.Debug($"Failed to remove recovered refresh-state backup: {e.Message}");
                }
            }

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            foreach (var staleTempPath in Directory.GetFiles(
                directory,
                Path.GetFileName(path) + ".tmp.*"))
            {
                try
                {
                    File.Delete(staleTempPath);
                }
                catch (Exception e)
                {
                    ConsoleLog.Debug($"Failed to remove stale refresh-state temp file: {e.Message}");
                }
            }
        }

        private static RefreshOperationState GetRefreshStateSnapshot()
        {
            lock (s_RefreshStateGate)
            {
                var state = s_CachedRefreshState ?? LoadRefreshState() ?? new RefreshOperationState();
                return CloneRefreshState(state);
            }
        }

        private static RefreshOperationState CloneRefreshState(RefreshOperationState state)
        {
            state ??= new RefreshOperationState();
            var clone = new RefreshOperationState
            {
                opId = state.opId ?? "",
                requestedAtUtc = state.requestedAtUtc ?? "",
                action = state.action ?? "",
                phase = state.phase ?? "",
                triggerStarted = state.triggerStarted,
                compileRequested = state.compileRequested,
                reloadObserved = state.reloadObserved,
                exitPlayModeRequested = state.exitPlayModeRequested,
                waitingForEditMode = state.waitingForEditMode,
                changedFiles = state.changedFiles == null
                    ? Array.Empty<string>()
                    : (string[])state.changedFiles.Clone(),
                changedFileCount = state.changedFiles?.Length ?? 0,
                generation = state.generation,
                effectivePort = Port,
                message = state.message ?? ""
            };
            clone.SyncPhaseFromSerialized();
            return clone;
        }

        private static bool TrySaveRefreshState(RefreshOperationState state, out string error)
        {
            error = "";
            try
            {
                state = NormalizeRefreshState(state);
                var persistedState = CloneRefreshState(state);
                var json = JsonUtility.ToJson(persistedState);
                var path = GetRefreshStatePath();
                lock (s_RefreshStateGate)
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    WriteRefreshStateDurably(path, json);
                    // Never publish a transition to /health until the same
                    // transition is durably visible to the next service epoch.
                    s_CachedRefreshState = CloneRefreshState(persistedState);
                }

                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                ConsoleLog.Warning($"Failed to write refresh state: {e}");
                return false;
            }
        }

        private static void WriteRefreshStateDurably(string path, string json)
        {
            var tempPath = path + $".tmp.{Guid.NewGuid():N}";
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(json ?? "");
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tempPath, path, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        ReplaceRefreshStateWithBackup(path, tempPath);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                        // Best-effort cleanup; the canonical file has already
                        // been durably written or the caller will fail closed.
                    }
                }
            }
        }

        private static void ReplaceRefreshStateWithBackup(string path, string tempPath)
        {
            // Compatibility path for Unity/Mono profiles without File.Replace.
            // Never truncate the canonical file. Both renames are same-volume,
            // and startup restores the old canonical from .backup if the
            // process stops between them.
            var backupPath = path + ".backup";
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            File.Move(path, backupPath);
            try
            {
                File.Move(tempPath, path);
            }
            catch
            {
                if (!File.Exists(path) && File.Exists(backupPath))
                {
                    File.Move(backupPath, path);
                }
                throw;
            }

            try
            {
                File.Delete(backupPath);
            }
            catch (Exception e)
            {
                // The new canonical is complete. Startup can safely discard
                // the older backup if immediate cleanup is unavailable.
                ConsoleLog.Debug($"Failed to remove refresh-state backup: {e.Message}");
            }
        }

        private static RefreshOperationState RecordRefreshPersistenceFailure(
            RefreshOperationState basis,
            string message)
        {
            var failedState =
                string.IsNullOrEmpty(basis?.opId) || (basis?.generation ?? 0) <= 0
                    ? CreateUnreadableRefreshState(
                        string.IsNullOrEmpty(message)
                            ? "Refresh state could not be durably recorded"
                            : message)
                    : CloneRefreshState(basis);
            if (!string.Equals(
                    failedState.action,
                    REFRESH_STATE_ERROR_ACTION,
                    StringComparison.Ordinal))
            {
                SetPhase(failedState, RefreshPhase.Failed);
                failedState.waitingForEditMode = false;
                failedState.message = string.IsNullOrEmpty(message)
                    ? "Refresh state could not be durably recorded"
                    : message;
            }

            if (!TrySaveRefreshState(failedState, out var failureError))
            {
                // The durable marker could not be written, but the live
                // service must still report an explicit failure rather than
                // the transition that failed to persist.
                lock (s_RefreshStateGate)
                {
                    s_CachedRefreshState = CloneRefreshState(failedState);
                }
                ConsoleLog.Warning(
                    $"Refresh failure marker is not durable: {failureError}. "
                    + "A later service epoch will treat the last persisted active state as interrupted.");
            }

            return failedState;
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

        private static bool TryUpdateRefreshState(
            string expectedOperationId,
            int expectedGeneration,
            Action<RefreshOperationState> update)
        {
            lock (s_RefreshStateGate)
            {
                var state = CloneRefreshState(
                    s_CachedRefreshState
                    ?? LoadRefreshState()
                    ?? new RefreshOperationState());
                if (!string.Equals(
                        state.opId,
                        expectedOperationId,
                        StringComparison.Ordinal)
                    || state.generation != expectedGeneration)
                {
                    return false;
                }

                update(state);
                if (TrySaveRefreshState(state, out var error))
                {
                    return true;
                }

                RecordRefreshPersistenceFailure(
                    state,
                    $"Refresh state transition could not be durably recorded; "
                    + $"the operation outcome may be unknown: {error}");
                return false;
            }
        }

        private static bool MarkRefreshFailed(
            string operationId,
            int generation,
            string message)
        {
            return TryUpdateRefreshState(operationId, generation, state =>
            {
                SetPhase(state, RefreshPhase.Failed);
                state.waitingForEditMode = false;
                state.message = message ?? "Refresh failed";
            });
        }

        private static bool MarkRefreshReady(
            string operationId,
            int generation,
            string message = null)
        {
            return TryUpdateRefreshState(operationId, generation, state =>
            {
                SetPhase(state, RefreshPhase.Ready);
                state.waitingForEditMode = false;
                state.message = string.IsNullOrEmpty(message) ? "Refresh completed" : message;
            });
        }

        private static bool IsTrackedRefreshOperation(RefreshOperationState state)
        {
            return state != null
                && !string.IsNullOrEmpty(s_TrackedRefreshOperationId)
                && string.Equals(
                    state.opId,
                    s_TrackedRefreshOperationId,
                    StringComparison.Ordinal)
                && state.generation == s_TrackedRefreshGeneration;
        }

        private static void TrackRefreshOperation(RefreshOperationState state)
        {
            if (state == null)
            {
                s_TrackedRefreshOperationId = "";
                s_TrackedRefreshGeneration = 0;
                return;
            }

            s_TrackedRefreshOperationId = state.opId ?? "";
            s_TrackedRefreshGeneration = state.generation;
            s_CompilationStartedObserved = false;
            s_AssemblyCompilationStartedObserved = false;
            s_CompilationFinishedObserved = false;
            s_CompileRequestedAtEditorTime = 0;
            s_CompileStartedAtEditorTime = 0;
            s_CompilationFinishedAtEditorTime = 0;
            s_PostCompileQuietUpdateCount = 0;
            if (DateTimeOffset.TryParseExact(
                    state.requestedAtUtc,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var requestedAt))
            {
                s_RefreshRequestedAtTicks = requestedAt.UtcDateTime.Ticks;
            }
        }

        private static double GetRefreshElapsedSeconds(RefreshOperationState state)
        {
            var requestedTicks = s_RefreshRequestedAtTicks;
            if (requestedTicks <= 0
                && DateTimeOffset.TryParseExact(
                    state?.requestedAtUtc,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var requestedAt))
            {
                requestedTicks = requestedAt.UtcDateTime.Ticks;
            }

            return requestedTicks <= 0
                ? double.MaxValue
                : Math.Max(
                    0,
                    (DateTime.UtcNow.Ticks - requestedTicks)
                    / (double)TimeSpan.TicksPerSecond);
        }

        private static void BeginCompilationObservation()
        {
            s_CompilationStartedObserved = false;
            s_AssemblyCompilationStartedObserved = false;
            s_CompilationFinishedObserved = false;
            s_CompileRequestedAtEditorTime = EditorApplication.timeSinceStartup;
            s_CompileStartedAtEditorTime = 0;
            s_CompilationFinishedAtEditorTime = 0;
            s_PostCompileQuietUpdateCount = 0;
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
            CompilationPipeline.assemblyCompilationStarted -= OnAssemblyCompilationStarted;
            CompilationPipeline.assemblyCompilationStarted += OnAssemblyCompilationStarted;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.playModeStateChanged -= OnRefreshPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnRefreshPlayModeStateChanged;
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;
            RefreshCompilationFlagCache();
        }

        private static async Task ProcessRefresh(HttpListenerContext context)
        {
            RefreshResponse responseData = null;
            var resultType = "system_error";
            var scheduleRefresh = false;
            var exitPlayModeRequested = false;
            var changedFiles = Array.Empty<string>();
            var requestBodyRead = false;
            var requestParsed = false;
            var acceptedOperationId = "";
            var acceptedGeneration = 0;
            try
            {
                var body = await ConsoleHttpServiceDependencies.ReadRequestBodyAsync(context);
                requestBodyRead = true;
                RefreshRequest request;
                if (string.IsNullOrWhiteSpace(body))
                {
                    request = new RefreshRequest();
                }
                else
                {
                    request = JsonUtility.FromJson<RefreshRequest>(body);
                    if (request == null)
                    {
                        throw new FormatException(
                            "Refresh request body is not a JSON object");
                    }
                }
                exitPlayModeRequested = request.exitPlayModeIfNeeded;
                changedFiles = request.changedFiles ?? Array.Empty<string>();
                foreach (var path in changedFiles)
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        throw new FormatException(
                            "Refresh changedFiles entries must be non-empty strings");
                    }
                }
                requestParsed = true;

                var current = GetRefreshStateSnapshot();
                if (IsActiveRefreshPhase(current.PhaseValue))
                {
                    responseData = new RefreshResponse
                    {
                        ok = false,
                        accepted = false,
                        sessionsCleared = false,
                        refreshing = true,
                        exitPlayModeRequested = false,
                        generation = current.generation,
                        message = "Refresh already in progress; this request was not accepted",
                        operation = CreatePublicRefreshState(current)
                    };
                }
                else if (s_CachedIsCompiling || s_CachedIsUpdating)
                {
                    responseData = new RefreshResponse
                    {
                        ok = false,
                        accepted = false,
                        sessionsCleared = false,
                        refreshing = false,
                        exitPlayModeRequested = false,
                        generation = current.generation,
                        message =
                            "Refresh was not accepted because Unity is already "
                            + "compiling or updating; wait for ready first",
                        operation = CreatePublicRefreshState(current)
                    };
                }
                else
                {
                    var nextGeneration = Mathf.Max(0, current.generation) + 1;
                    var requestedAt = DateTimeOffset.UtcNow;
                    var requestedAtUtc = requestedAt.ToString("O");
                    var opId = Guid.NewGuid().ToString("N");
                    var nextState = new RefreshOperationState
                    {
                        opId = opId,
                        requestedAtUtc = requestedAtUtc,
                        action = REFRESH_ACTION,
                        triggerStarted = false,
                        compileRequested = true,
                        reloadObserved = false,
                        exitPlayModeRequested = exitPlayModeRequested,
                        waitingForEditMode = false,
                        changedFiles = (string[])changedFiles.Clone(),
                        changedFileCount = changedFiles.Length,
                        generation = nextGeneration,
                        message = "Refresh requested",
                        PhaseValue = RefreshPhase.Requested
                    };
                    // Initialize every in-memory dependency before publishing
                    // Requested to /health. Main-thread callbacks may observe
                    // the cached state immediately after this durable write.
                    s_RefreshRequestedAtTicks = requestedAt.Ticks;
                    if (!TrySaveRefreshState(nextState, out var acceptancePersistenceError))
                    {
                        var failureMessage =
                            "Refresh request was not accepted because its state could not be "
                            + $"durably recorded: {acceptancePersistenceError}";
                        responseData = new RefreshResponse
                        {
                            ok = false,
                            accepted = false,
                            sessionsCleared = false,
                            refreshing = false,
                            exitPlayModeRequested = false,
                            generation = current.generation,
                            message = failureMessage,
                            operation = CreatePublicRefreshState(current)
                        };
                    }
                    else
                    {
                        acceptedOperationId = nextState.opId;
                        acceptedGeneration = nextState.generation;
                        TrackRefreshOperation(nextState);
                        ClearSessionState();

                        responseData = new RefreshResponse
                        {
                            ok = true,
                            accepted = true,
                            sessionsCleared = true,
                            refreshing = true,
                            exitPlayModeRequested = exitPlayModeRequested,
                            generation = nextState.generation,
                            message = "Refresh and script compilation scheduled. Existing compiler/executor sessions were cleared.",
                            operation = CreatePublicRefreshState(nextState)
                        };
                        resultType = "ok";
                        scheduleRefresh = true;
                    }
                }
            }
            catch (Exception e)
            {
                var accepted = !string.IsNullOrEmpty(acceptedOperationId);
                if (accepted)
                {
                    MarkRefreshFailed(
                        acceptedOperationId,
                        acceptedGeneration,
                        e.ToString());
                }
                resultType =
                    requestBodyRead && !requestParsed
                        ? "validation_error"
                        : "system_error";
                var current = GetRefreshStateSnapshot();
                responseData = new RefreshResponse
                {
                    ok = false,
                    accepted = accepted,
                    sessionsCleared = false,
                    refreshing = IsActiveRefreshPhase(current.PhaseValue),
                    exitPlayModeRequested = accepted && exitPlayModeRequested,
                    generation = current.generation,
                    message = e.ToString(),
                    operation = CreatePublicRefreshState(current)
                };
            }

            var ok = responseData.ok;
            var summary = responseData.message ?? (ok ? "Refresh accepted" : "Refresh failed");
            var envelope = s_EnvelopeFactory.CreateEnvelope(ok, "bootstrap", resultType, summary, "", JsonUtility.ToJson(responseData));
            var invocationClaim = ConsoleHttpServiceDependencies.GetInvocationClaim(context);
            await WriteEnvelopeResponseAsync(context, envelope, "Refresh");

            if (scheduleRefresh)
            {
                var acceptanceIsDurable = invocationClaim == null
                    || string.IsNullOrEmpty(invocationClaim.InvocationId)
                    || invocationClaim.TerminalResponsePersisted;
                if (acceptanceIsDurable)
                {
                    // Persist the acceptance before any operation that can
                    // trigger a domain reload. This prevents an accepted
                    // refresh from being downgraded to an unknown invocation
                    // solely because its own reload closed the HTTP response.
                    var scheduledOperationId = responseData.operation?.opId ?? "";
                    var scheduledGeneration = responseData.operation?.generation ?? 0;
                    MainThreadRequestRunner.Post(() => TriggerRefresh(
                        scheduledOperationId,
                        scheduledGeneration));
                }
                else
                {
                    MarkRefreshFailed(
                        acceptedOperationId,
                        acceptedGeneration,
                        "Refresh acceptance could not be durably recorded; refresh was not started");
                }
            }
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

        private static void TriggerRefresh(
            string expectedOperationId,
            int expectedGeneration)
        {
            var requestedState = GetRefreshStateSnapshot();
            if (!string.Equals(
                    requestedState.opId,
                    expectedOperationId,
                    StringComparison.Ordinal)
                || requestedState.generation != expectedGeneration
                || requestedState.PhaseValue != RefreshPhase.Requested
                || requestedState.triggerStarted)
            {
                ConsoleLog.Warning(
                    $"Skipped stale refresh trigger for operation "
                    + $"{expectedOperationId}/{expectedGeneration}; current operation is "
                    + $"{requestedState.opId}/{requestedState.generation} "
                    + $"({requestedState.phase}, triggerStarted={requestedState.triggerStarted}).");
                return;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                MarkRefreshFailed(
                    expectedOperationId,
                    expectedGeneration,
                    "Refresh trigger did not start because Unity became busy "
                    + "with unrelated compilation or asset updates");
                return;
            }

            var shouldExitPlayMode =
                requestedState.exitPlayModeRequested
                && (
                    EditorApplication.isPlaying
                    || EditorApplication.isPlayingOrWillChangePlaymode
                );
            if (!TryUpdateRefreshState(
                    expectedOperationId,
                    expectedGeneration,
                    state =>
                    {
                        state.triggerStarted = true;
                        state.waitingForEditMode = shouldExitPlayMode;
                        state.message = shouldExitPlayMode
                            ? "Exiting Play Mode before refresh"
                            : "Refresh trigger started";
                    }))
            {
                ConsoleLog.Warning(
                    $"Refresh trigger state for {expectedOperationId}/"
                    + $"{expectedGeneration} could not be durably recorded. "
                    + "No Play Mode exit or asset refresh was dispatched.");
                return;
            }

            try
            {
                if (shouldExitPlayMode)
                {
                    EditorApplication.isPlaying = false;
                    return;
                }

                ContinueRefresh(expectedOperationId, expectedGeneration);
            }
            catch (Exception e)
            {
                MarkRefreshFailed(
                    expectedOperationId,
                    expectedGeneration,
                    e.ToString());
                ConsoleLog.Warning($"Refresh failed: {e}");
            }
        }

        private static void ContinueRefresh(
            string expectedOperationId,
            int expectedGeneration)
        {
            var state = GetRefreshStateSnapshot();
            if (!string.Equals(
                    state.opId,
                    expectedOperationId,
                    StringComparison.Ordinal)
                || state.generation != expectedGeneration
                || state.PhaseValue != RefreshPhase.Requested
                || !state.triggerStarted
                || state.waitingForEditMode)
            {
                return;
            }
            if (state.exitPlayModeRequested
                && (
                    EditorApplication.isPlaying
                    || EditorApplication.isPlayingOrWillChangePlaymode
                ))
            {
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                // The expected Play Mode transition can briefly compile or
                // update assets. OnEditorUpdate resumes this same operation
                // once the Editor is idle; it never creates a second intent.
                return;
            }

            try
            {
                var files = state.changedFiles ?? Array.Empty<string>();
                if (files.Length > 0)
                {
                    TriggerRefreshTargeted(
                        expectedOperationId,
                        expectedGeneration,
                        files);
                }
                else
                {
                    TriggerRefreshFull(
                        expectedOperationId,
                        expectedGeneration);
                }
            }
            catch (Exception e)
            {
                MarkRefreshFailed(
                    expectedOperationId,
                    expectedGeneration,
                    e.ToString());
                ConsoleLog.Warning($"Refresh failed: {e}");
            }
        }

        /// <summary>
        /// Targeted refresh: caller provides exact file paths.
        /// Fast — no directory scanning, works for any path (Assets/, Packages/, etc.).
        /// </summary>
        private static void TriggerRefreshTargeted(
            string expectedOperationId,
            int expectedGeneration,
            string[] files)
        {
            var scriptCount = 0;
            foreach (var path in files)
            {
                if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    scriptCount++;
                }
            }
            var otherCount = files.Length - scriptCount;

            if (!TryUpdateRefreshState(
                    expectedOperationId,
                    expectedGeneration,
                    state =>
                    {
                        state.compileRequested = true;
                        SetPhase(state, RefreshPhase.RefreshingAssets);
                        state.message =
                            $"Importing {files.Length} file(s) before compilation";
                    }))
            {
                return;
            }
            BeginCompilationObservation();

            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (var path in files)
                {
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            var afterImport = GetRefreshStateSnapshot();
            if (!IsTrackedRefreshOperation(afterImport)
                || !string.Equals(
                    afterImport.opId,
                    expectedOperationId,
                    StringComparison.Ordinal)
                || afterImport.generation != expectedGeneration
                || !IsActiveRefreshPhase(afterImport.PhaseValue))
            {
                return;
            }

            if (EditorApplication.isCompiling
                && !s_CompilationStartedObserved)
            {
                s_CompilationStartedObserved = true;
                s_CompilationFinishedObserved = false;
                s_CompileStartedAtEditorTime =
                    EditorApplication.timeSinceStartup;
                s_PostCompileQuietUpdateCount = 0;
                TryUpdateRefreshState(
                    expectedOperationId,
                    expectedGeneration,
                    state =>
                    {
                        SetPhase(state, RefreshPhase.Compiling);
                        state.message = "Script compilation is in progress";
                    });
            }

            if (s_CompilationStartedObserved
                || s_CompilationFinishedObserved
                || afterImport.PhaseValue == RefreshPhase.Compiling
                || EditorApplication.isCompiling)
            {
                TryUpdateRefreshState(
                    expectedOperationId,
                    expectedGeneration,
                    state =>
                    {
                        if (state.PhaseValue == RefreshPhase.RefreshingAssets)
                        {
                            state.message =
                                $"Waiting for compilation ({scriptCount} script(s), "
                                + $"{otherCount} other asset(s) imported)";
                        }
                    });
                return;
            }

            TryUpdateRefreshState(
                expectedOperationId,
                expectedGeneration,
                state =>
                {
                    state.message =
                        $"Requesting compilation ({scriptCount} script(s), "
                        + $"{otherCount} other asset(s) imported)";
                });
            s_CompileRequestedAtEditorTime =
                EditorApplication.timeSinceStartup;
            CompilationPipeline.RequestScriptCompilation();
        }

        /// <summary>
        /// Full refresh: no file list provided.
        /// Activates the editor window to flush file-watcher events, then
        /// lets AssetDatabase.Refresh() handle everything — detection, import,
        /// compilation, and domain reload are all managed by Unity.
        /// </summary>
        private static void TriggerRefreshFull(
            string expectedOperationId,
            int expectedGeneration)
        {
            if (!TryUpdateRefreshState(
                    expectedOperationId,
                    expectedGeneration,
                    state =>
                    {
                        state.compileRequested = true;
                        SetPhase(state, RefreshPhase.RefreshingAssets);
                        state.message = "Activating editor and refreshing assets";
                    }))
            {
                return;
            }
            BeginCompilationObservation();

            // Bring editor to foreground so the OS file-watcher queue is flushed.
            // Without this, Refresh() misses external changes when Unity is in the background.
            ActivateEditorWindow();

            // Unity handles everything: detect changes, import, trigger compilation.
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var afterRefresh = GetRefreshStateSnapshot();
            if (!IsTrackedRefreshOperation(afterRefresh)
                || !string.Equals(
                    afterRefresh.opId,
                    expectedOperationId,
                    StringComparison.Ordinal)
                || afterRefresh.generation != expectedGeneration
                || !IsActiveRefreshPhase(afterRefresh.PhaseValue))
            {
                return;
            }

            if (EditorApplication.isCompiling
                && !s_CompilationStartedObserved)
            {
                s_CompilationStartedObserved = true;
                s_CompilationFinishedObserved = false;
                s_CompileStartedAtEditorTime =
                    EditorApplication.timeSinceStartup;
                s_PostCompileQuietUpdateCount = 0;
                TryUpdateRefreshState(
                    expectedOperationId,
                    expectedGeneration,
                    state =>
                    {
                        SetPhase(state, RefreshPhase.Compiling);
                        state.message = "Compiling after full asset refresh";
                    });
            }

            if (s_CompilationStartedObserved
                || s_CompilationFinishedObserved
                || afterRefresh.PhaseValue == RefreshPhase.Compiling
                || EditorApplication.isCompiling)
            {
                TryUpdateRefreshState(
                    expectedOperationId,
                    expectedGeneration,
                    state =>
                    {
                        if (state.PhaseValue == RefreshPhase.RefreshingAssets)
                        {
                            state.message =
                                "Waiting for compilation after full asset refresh";
                        }
                    });
                return;
            }

            TryUpdateRefreshState(
                expectedOperationId,
                expectedGeneration,
                state =>
                {
                    state.message = "Waiting for compilation after full asset refresh";
                });

            // A full `refresh and compile` request must not infer "no compile"
            // from one transient false isCompiling sample.
            s_CompileRequestedAtEditorTime =
                EditorApplication.timeSinceStartup;
            CompilationPipeline.RequestScriptCompilation();
        }

        // ImportChangedScripts and timestamp persistence removed —
        // full-refresh mode now uses ActivateEditorWindow() + AssetDatabase.Refresh()
        // which lets Unity handle all detection, import, and compilation natively.
        // Targeted mode (changedFiles) uses ImportAsset directly in TriggerRefreshTargeted.

        private static void OnCompilationStarted(object _)
        {
            var state = GetRefreshStateSnapshot();
            if (!IsTrackedRefreshOperation(state)
                || !state.triggerStarted
                || (
                    state.PhaseValue != RefreshPhase.RefreshingAssets
                    && state.PhaseValue != RefreshPhase.Compiling
                ))
            {
                return;
            }

            s_CompilationStartedObserved = true;
            s_AssemblyCompilationStartedObserved = false;
            s_CompilationFinishedObserved = false;
            s_CompileStartedAtEditorTime =
                EditorApplication.timeSinceStartup;
            s_CompilationFinishedAtEditorTime = 0;
            s_PostCompileQuietUpdateCount = 0;
            TryUpdateRefreshState(
                state.opId,
                state.generation,
                current =>
                {
                    if (current.triggerStarted
                        && (
                            current.PhaseValue == RefreshPhase.RefreshingAssets
                            || current.PhaseValue == RefreshPhase.Compiling
                        ))
                    {
                        SetPhase(current, RefreshPhase.Compiling);
                        current.message = "Script compilation started";
                    }
                });
        }

        private static void OnAssemblyCompilationStarted(string _)
        {
            var state = GetRefreshStateSnapshot();
            if (IsTrackedRefreshOperation(state)
                && state.triggerStarted
                && (
                    state.PhaseValue == RefreshPhase.RefreshingAssets
                    || state.PhaseValue == RefreshPhase.Compiling
                ))
            {
                s_AssemblyCompilationStartedObserved = true;
            }
        }

        private static void OnCompilationFinished(object _)
        {
            var state = GetRefreshStateSnapshot();
            if (!IsTrackedRefreshOperation(state)
                || !state.triggerStarted
                || (
                    state.PhaseValue != RefreshPhase.RefreshingAssets
                    && state.PhaseValue != RefreshPhase.Compiling
                ))
            {
                return;
            }

            s_CompilationStartedObserved = true;
            s_CompilationFinishedObserved = true;
            s_CompilationFinishedAtEditorTime =
                EditorApplication.timeSinceStartup;
            s_PostCompileQuietUpdateCount = 0;
            TryUpdateRefreshState(
                state.opId,
                state.generation,
                current =>
                {
                    if (current.triggerStarted
                        && (
                            current.PhaseValue == RefreshPhase.RefreshingAssets
                            || current.PhaseValue == RefreshPhase.Compiling
                        ))
                    {
                        SetPhase(current, RefreshPhase.Compiling);
                        current.message =
                            s_AssemblyCompilationStartedObserved
                                ? "Script compilation finished, waiting for assembly reload"
                                : "No assemblies required compilation; waiting for stable idle";
                    }
                });
        }

        private static void OnBeforeAssemblyReload()
        {
            var state = GetRefreshStateSnapshot();
            if (state.triggerStarted
                && (
                    state.PhaseValue == RefreshPhase.RefreshingAssets
                    || state.PhaseValue == RefreshPhase.Compiling
                ))
            {
                TryUpdateRefreshState(
                    state.opId,
                    state.generation,
                    current =>
                    {
                        if (current.triggerStarted
                            && (
                                current.PhaseValue == RefreshPhase.RefreshingAssets
                                || current.PhaseValue == RefreshPhase.Compiling
                            ))
                        {
                            SetPhase(current, RefreshPhase.Reloading);
                            current.message = "Assembly reload started";
                        }
                    });
            }

            // Stop listener before domain unload to prevent port leak and drift.
            Shutdown();
        }

        private static void OnAfterAssemblyReload()
        {
            var state = GetRefreshStateSnapshot();
            if (state.PhaseValue == RefreshPhase.Reloading
                && state.triggerStarted)
            {
                TryUpdateRefreshState(
                    state.opId,
                    state.generation,
                    current =>
                    {
                        if (current.PhaseValue == RefreshPhase.Reloading
                            && current.triggerStarted)
                        {
                            current.reloadObserved = true;
                            SetPhase(current, RefreshPhase.Ready);
                            current.message = "Assembly reload finished";
                        }
                    });
            }
            else if (IsActiveRefreshPhase(state.PhaseValue)
                && !(
                    state.PhaseValue == RefreshPhase.Requested
                    && state.triggerStarted
                    && state.exitPlayModeRequested
                ))
            {
                MarkRefreshFailed(
                    state.opId,
                    state.generation,
                    "Assembly reload interrupted refresh before its reload phase");
            }
        }

        private static void OnRefreshPlayModeStateChanged(
            PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredEditMode)
            {
                return;
            }

            var state = GetRefreshStateSnapshot();
            if (state.PhaseValue == RefreshPhase.Requested
                && state.triggerStarted
                && state.exitPlayModeRequested
                && state.waitingForEditMode)
            {
                TryUpdateRefreshState(
                    state.opId,
                    state.generation,
                    current =>
                    {
                        if (current.PhaseValue == RefreshPhase.Requested
                            && current.triggerStarted
                            && current.exitPlayModeRequested)
                        {
                            current.waitingForEditMode = false;
                            current.message =
                                "Play Mode exited; waiting for Unity to become idle";
                        }
                    });
            }
        }

        private static void OnEditorUpdate()
        {
            RefreshCompilationFlagCache();

            var state = GetRefreshStateSnapshot();
            if (!IsActiveRefreshPhase(state.PhaseValue))
            {
                return;
            }

            if (state.PhaseValue == RefreshPhase.Requested)
            {
                var elapsedSeconds = GetRefreshElapsedSeconds(state);
                if (!state.triggerStarted)
                {
                    if (elapsedSeconds >= REFRESH_TRIGGER_TIMEOUT_SECONDS)
                    {
                        MarkRefreshFailed(
                            state.opId,
                            state.generation,
                            "Refresh trigger did not start before timeout");
                    }
                    return;
                }

                if (!state.exitPlayModeRequested)
                {
                    if (elapsedSeconds >= REFRESH_TRIGGER_TIMEOUT_SECONDS)
                    {
                        MarkRefreshFailed(
                            state.opId,
                            state.generation,
                            "Refresh trigger stopped before asset refresh began");
                    }
                    return;
                }

                if (elapsedSeconds >= REFRESH_EXIT_PLAYMODE_TIMEOUT_SECONDS)
                {
                    MarkRefreshFailed(
                        state.opId,
                        state.generation,
                        "Timed out exiting Play Mode and waiting for an idle Editor");
                    return;
                }

                var editModeStable =
                    !EditorApplication.isPlaying
                    && !EditorApplication.isPlayingOrWillChangePlaymode;
                if (state.waitingForEditMode)
                {
                    if (editModeStable)
                    {
                        TryUpdateRefreshState(
                            state.opId,
                            state.generation,
                            current =>
                            {
                                if (current.PhaseValue == RefreshPhase.Requested
                                    && current.triggerStarted
                                    && current.exitPlayModeRequested)
                                {
                                    current.waitingForEditMode = false;
                                    current.message =
                                        "Play Mode exited; waiting for Unity to become idle";
                                }
                            });
                    }
                    return;
                }

                if (!editModeStable
                    || EditorApplication.isCompiling
                    || EditorApplication.isUpdating)
                {
                    return;
                }

                ContinueRefresh(state.opId, state.generation);
                return;
            }

            if (!IsTrackedRefreshOperation(state))
            {
                MarkRefreshFailed(
                    state.opId,
                    state.generation,
                    "Refresh lifecycle lost its operation binding");
                return;
            }

            if (EditorApplication.isCompiling)
            {
                if (!s_CompilationStartedObserved
                    || s_CompilationFinishedObserved)
                {
                    s_CompilationStartedObserved = true;
                    s_AssemblyCompilationStartedObserved = false;
                    s_CompilationFinishedObserved = false;
                    s_CompileStartedAtEditorTime =
                        EditorApplication.timeSinceStartup;
                    s_CompilationFinishedAtEditorTime = 0;
                    s_PostCompileQuietUpdateCount = 0;
                }
                TryUpdateRefreshState(
                    state.opId,
                    state.generation,
                    current =>
                    {
                        if (current.triggerStarted
                            && (
                                current.PhaseValue == RefreshPhase.RefreshingAssets
                                || current.PhaseValue == RefreshPhase.Compiling
                            ))
                        {
                            SetPhase(current, RefreshPhase.Compiling);
                            current.message = "Script compilation in progress";
                        }
                    });
                TryFailRefreshForCompilationTimeout(state);
                return;
            }

            if (EditorApplication.isUpdating)
            {
                s_PostCompileQuietUpdateCount = 0;
                return;
            }

            if (state.PhaseValue == RefreshPhase.RefreshingAssets)
            {
                if (!state.compileRequested)
                {
                    MarkRefreshReady(
                        state.opId,
                        state.generation,
                        "Asset refresh completed without script compilation");
                    return;
                }

                if (EditorApplication.timeSinceStartup
                    - s_CompileRequestedAtEditorTime
                    >= REFRESH_COMPILE_START_TIMEOUT_SECONDS)
                {
                    MarkRefreshFailed(
                        state.opId,
                        state.generation,
                        "Script compilation was requested but did not start before timeout");
                }
                return;
            }

            if (state.PhaseValue != RefreshPhase.Compiling)
            {
                return;
            }

            if (!s_CompilationFinishedObserved)
            {
                TryFailRefreshForCompilationTimeout(state);
                return;
            }

            if (s_CachedCompileFailed)
            {
                MarkRefreshFailed(
                    state.opId,
                    state.generation,
                    "Script compilation failed");
                return;
            }

            var finishedElapsed =
                EditorApplication.timeSinceStartup
                - s_CompilationFinishedAtEditorTime;
            if (s_AssemblyCompilationStartedObserved)
            {
                // Unity 2022 loads assemblies after a successful compilation.
                // beforeAssemblyReload is the authoritative transition. A
                // successful build that never reloads is not safe to call ready.
                if (finishedElapsed >= REFRESH_RELOAD_START_TIMEOUT_SECONDS)
                {
                    MarkRefreshFailed(
                        state.opId,
                        state.generation,
                        "Compilation finished but assembly reload did not start before timeout");
                }
                return;
            }

            s_PostCompileQuietUpdateCount++;
            if (finishedElapsed >= REFRESH_POST_COMPILE_QUIET_SECONDS
                && s_PostCompileQuietUpdateCount
                    >= REFRESH_POST_COMPILE_QUIET_UPDATES)
            {
                MarkRefreshReady(
                    state.opId,
                    state.generation,
                    "Compilation check completed; no assembly required rebuilding");
            }
        }

        private static bool TryFailRefreshForCompilationTimeout(
            RefreshOperationState state)
        {
            var compileStartedAt =
                s_CompileStartedAtEditorTime > 0
                    ? s_CompileStartedAtEditorTime
                    : s_CompileRequestedAtEditorTime;
            if (compileStartedAt <= 0
                || EditorApplication.timeSinceStartup - compileStartedAt
                    < REFRESH_COMPILE_FINISH_TIMEOUT_SECONDS)
            {
                return false;
            }

            MarkRefreshFailed(
                state.opId,
                state.generation,
                "Script compilation did not finish before timeout");
            return true;
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
            var parentInvocation =
                ConsoleHttpServiceDependencies.GetInvocationClaim(context);

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

                    result = await ForwardReset(
                        targetIP,
                        targetPort,
                        uuid,
                        parentInvocation);
                }
                else
                {
                    var compiler = s_ReplServiceRegistry.FetchRuntimeREPLCompiler(uuid, runtimeDllPath, s_RuntimeREPLCompilerGenerator);
                    var (compileBytes, compileScriptClsName, errorMsg) = compiler.Compile(code, defines, defaultUsing);
                    var compilerNotice = ConsumeCompilerNotice(compiler);
                    if (!string.IsNullOrEmpty(errorMsg))
                    {
                        result = $"Compile failed: {errorMsg}";
                    }
                    else if (compileBytes == null)
                    {
                        result = compilerNotice;
                    }
                    else
                    {
                        var executeResult = await ForwardDllToPlayer(
                            targetIP,
                            targetPort,
                            uuid,
                            compileBytes,
                            compileScriptClsName,
                            parentInvocation);
                        result = CombineCompilerNotice(compilerNotice, executeResult);
                    }
                }
            }
            catch (MainThreadOutcomeUnknownException e)
            {
                var unknownEnvelope = CreateOutcomeUnknownEnvelope("execute", e, uuid);
                await WriteEnvelopeResponseAsync(context, unknownEnvelope, "RuntimeCompile");
                return;
            }
            catch (RemoteInvocationOutcomeUnknownException e)
            {
                var unknownEnvelope = CreateOutcomeUnknownEnvelope("execute", e, uuid);
                await WriteEnvelopeResponseAsync(context, unknownEnvelope, "RuntimeCompile");
                return;
            }
            catch (Exception e)
            {
                result = $"Compile failed, {e}";
            }

            var envelope = s_EnvelopeFactory.CreateTextEnvelope("execute", result, uuid);
            await WriteEnvelopeResponseAsync(context, envelope, "RuntimeCompile");
        }

        private static string ParseExecuteResponseText(
            string responseText,
            string expectedInvocationId = "",
            string expectedTargetId = "",
            string expectedRequestDigest = "")
        {
            if (string.IsNullOrEmpty(responseText))
            {
                if (!string.IsNullOrEmpty(expectedInvocationId))
                {
                    throw new RemoteInvocationOutcomeUnknownException(
                        $"Player child invocation {expectedInvocationId} returned an empty response; its outcome is unknown.");
                }
                return string.Empty;
            }

            try
            {
                var envelope = JsonUtility.FromJson<HttpResponseEnvelope>(responseText);
                if (envelope != null && !string.IsNullOrEmpty(envelope.stage) && envelope.dataJson != null)
                {
                    if (!string.IsNullOrEmpty(expectedInvocationId))
                    {
                        ValidatePlayerInvocationReceipt(
                            envelope,
                            expectedInvocationId,
                            expectedTargetId,
                            expectedRequestDigest);
                    }

                    if (!envelope.ok
                        && (
                            string.Equals(envelope.type, "outcome_unknown", StringComparison.Ordinal)
                            || string.Equals(envelope.type, "operation_in_progress", StringComparison.Ordinal)
                            || string.Equals(envelope.type, "invocation_conflict", StringComparison.Ordinal)
                        ))
                    {
                        throw new RemoteInvocationOutcomeUnknownException(
                            $"Player child invocation "
                            + $"{(string.IsNullOrEmpty(expectedInvocationId) ? "<legacy>" : expectedInvocationId)} "
                            + (string.IsNullOrEmpty(envelope.summary)
                                ? "is unresolved."
                                : envelope.summary));
                    }

                    if (!envelope.ok)
                    {
                        return ConsoleLog.Format(
                            $"Forward failed: {envelope.summary ?? envelope.type ?? "Player request failed"}");
                    }

                    var data = JsonUtility.FromJson<TextResponseData>(envelope.dataJson);
                    return string.IsNullOrEmpty(data?.text)
                        ? envelope.summary ?? string.Empty
                        : data.text;
                }
            }
            catch (RemoteInvocationOutcomeUnknownException)
            {
                throw;
            }
            catch (Exception e)
            {
                if (!string.IsNullOrEmpty(expectedInvocationId))
                {
                    throw new RemoteInvocationOutcomeUnknownException(
                        $"Player child invocation {expectedInvocationId} returned "
                        + $"an unreadable protected response; its outcome is unknown: {e.Message}",
                        e);
                }
                ConsoleLog.Warning($"Failed to parse execute response envelope JSON: {e}");
            }

            if (!string.IsNullOrEmpty(expectedInvocationId))
            {
                throw new RemoteInvocationOutcomeUnknownException(
                    $"Player child invocation {expectedInvocationId} returned no "
                    + "protocol-v2 envelope; its outcome is unknown.");
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

        private static void ValidatePlayerInvocationReceipt(
            HttpResponseEnvelope envelope,
            string expectedInvocationId,
            string expectedTargetId,
            string expectedRequestDigest)
        {
            var receipt = envelope?.invocation;
            if (receipt == null
                || !Guid.TryParse(receipt.invocationId, out var parsedReceiptId)
                || !Guid.TryParse(expectedInvocationId, out var parsedExpectedId)
                || parsedReceiptId != parsedExpectedId
                || !string.Equals(receipt.endpoint, "execute", StringComparison.Ordinal)
                || !string.Equals(
                    receipt.requestDigest,
                    expectedRequestDigest,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    receipt.guarantee,
                    "at-most-once",
                    StringComparison.Ordinal)
                || receipt.dedupeWindowSeconds <= 0)
            {
                throw new RemoteInvocationOutcomeUnknownException(
                    $"Player child invocation {expectedInvocationId} returned no "
                    + "matching durable receipt; its outcome is unknown.");
            }

            if (string.Equals(receipt.state, "rejected", StringComparison.Ordinal))
            {
                // A target replacement between health and execute is known not
                // to have run this request. The rejection receipt identifies
                // the new service target, so it need not match expectedTargetId.
                return;
            }

            if (!string.Equals(
                    receipt.targetId,
                    expectedTargetId,
                    StringComparison.Ordinal))
            {
                throw new RemoteInvocationOutcomeUnknownException(
                    $"Player child invocation {expectedInvocationId} receipt "
                    + "belongs to a different target; its outcome is unknown.");
            }
        }

        private static async Task<string> ForwardDllToPlayer(
            string ip,
            string port,
            string uuid,
            byte[] dllBytes,
            string className,
            InvocationClaim parentInvocation)
        {
            var request = new ExecuteREPLRequest
            {
                dllBase64 = Convert.ToBase64String(dllBytes),
                className = className,
                uuid = uuid,
                reset = false
            };
            return await PostToPlayer(
                ip,
                port,
                request,
                "DLL",
                parentInvocation);
        }

        private static async Task<string> ForwardReset(
            string ip,
            string port,
            string uuid,
            InvocationClaim parentInvocation)
        {
            var request = new ForwardResetRequest
            {
                uuid = uuid,
                reset = true
            };
            return await PostToPlayer(
                ip,
                port,
                request,
                "reset",
                parentInvocation);
        }

        private static async Task<string> PostToPlayer<T>(
            string ip,
            string port,
            T request,
            string debugLabel,
            InvocationClaim parentInvocation)
        {
            var url = $"http://{ip}:{port}/CSharpConsole/execute";
            var jsonBytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(request));
            var protectedForward = parentInvocation != null
                && !string.IsNullOrEmpty(parentInvocation.InvocationId);
            var childInvocationId = "";
            var playerTargetId = "";
            var requestDigest = "";
            if (protectedForward)
            {
                var playerHealth = await ProbePlayerReliability(ip, port);
                playerTargetId = playerHealth.targetId;
                childInvocationId = DerivePlayerChildInvocationId(
                    parentInvocation.InvocationId,
                    debugLabel);
                requestDigest = ComputeSha256Hex(jsonBytes);
            }

            using var requestMessage = new HttpRequestMessage(
                HttpMethod.Post,
                url);
            requestMessage.Content = new ByteArrayContent(jsonBytes);
            requestMessage.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            if (protectedForward)
            {
                requestMessage.Headers.TryAddWithoutValidation(
                    InvocationCoordinator.InvocationIdHeader,
                    childInvocationId);
                requestMessage.Headers.TryAddWithoutValidation(
                    InvocationCoordinator.TargetIdHeader,
                    playerTargetId);
            }

            HttpResponseMessage response;
            try
            {
                response = await s_HttpClient.SendAsync(requestMessage);
            }
            catch (Exception ex)
            {
                throw new RemoteInvocationOutcomeUnknownException(
                    protectedForward
                        ? $"Player child invocation {childInvocationId} may have "
                            + $"executed but no response was received: {ex.Message}"
                        : $"Player request may have executed but no response was received: {ex.Message}",
                    ex);
            }

            using (response)
            {
                string responseText;
                try
                {
                    responseText = await response.Content.ReadAsStringAsync();
                }
                catch (Exception ex)
                {
                    throw new RemoteInvocationOutcomeUnknownException(
                        protectedForward
                            ? $"Player child invocation {childInvocationId} may "
                                + $"have executed but its response could not be read: {ex.Message}"
                            : $"Player request may have executed but its response could not be read: {ex.Message}",
                        ex);
                }

                var executeText = ParseExecuteResponseText(
                    responseText,
                    childInvocationId,
                    playerTargetId,
                    requestDigest);
                if (!response.IsSuccessStatusCode)
                {
                    return ConsoleLog.Format($"Forward failed: {(int)response.StatusCode} {response.ReasonPhrase}: {executeText}");
                }

                ConsoleLog.Debug($"Forwarded {debugLabel} to {ip}:{port}, response={responseText}");
                return executeText;
            }
        }

        private static async Task<HealthResponse> ProbePlayerReliability(
            string ip,
            string port)
        {
            var url = $"http://{ip}:{port}/CSharpConsole/health";
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}"));
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            HttpResponseMessage response;
            try
            {
                response = await s_HttpClient.PostAsync(url, content);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    $"Player reliability preflight failed before execute: {e.Message}",
                    e);
            }

            using (response)
            {
                var responseText = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"Player reliability preflight returned HTTP "
                        + $"{(int)response.StatusCode}: {responseText}");
                }

                var envelope = JsonUtility.FromJson<HttpResponseEnvelope>(responseText);
                var health = envelope == null
                    || !envelope.ok
                    || string.IsNullOrWhiteSpace(envelope.dataJson)
                    ? null
                    : JsonUtility.FromJson<HealthResponse>(envelope.dataJson);
                if (health == null
                    || !health.initialized
                    || health.isEditor
                    || health.protocolVersion < 2
                    || string.IsNullOrEmpty(health.targetId)
                    || !health.targetId.StartsWith("player-", StringComparison.Ordinal)
                    || !health.journalWritable
                    || health.dedupeWindowSeconds <= 0
                    || string.IsNullOrEmpty(health.unityVersion)
                    || !health.unityVersion.StartsWith("2022.", StringComparison.Ordinal)
                    || health.mainThreadHeartbeatAgeMs < 0
                    || health.mainThreadHeartbeatAgeMs > 5000
                    || !HasReliabilityCapabilities(health.capabilities))
                {
                    throw new InvalidOperationException(
                        "Player reliability preflight did not prove a ready "
                        + "Unity 2022 protocol-v2 target with a writable journal.");
                }

                return health;
            }
        }

        private static bool HasReliabilityCapabilities(string[] capabilities)
        {
            var required = new[]
            {
                "invocation_headers",
                "invocation_receipts",
                "invocation_status",
                "at_most_once"
            };
            foreach (var expected in required)
            {
                var found = false;
                foreach (var capability in capabilities ?? Array.Empty<string>())
                {
                    if (string.Equals(
                        capability,
                        expected,
                        StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return false;
                }
            }

            return true;
        }

        private static string DerivePlayerChildInvocationId(
            string parentInvocationId,
            string debugLabel)
        {
            if (!Guid.TryParse(parentInvocationId, out var parsedParent))
            {
                throw new InvalidOperationException(
                    "Protected runtime forwarding requires a valid parent invocation id.");
            }

            var material = Encoding.UTF8.GetBytes(
                parsedParent.ToString("D")
                + "\nplayer/execute\n"
                + (debugLabel ?? ""));
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var digest = sha256.ComputeHash(material);
            var guidBytes = new byte[16];
            Array.Copy(digest, guidBytes, guidBytes.Length);
            return new Guid(guidBytes).ToString("D");
        }

        private static string ComputeSha256Hex(byte[] bytes)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var digest = sha256.ComputeHash(bytes ?? Array.Empty<byte>());
            var builder = new StringBuilder(digest.Length * 2);
            foreach (var value in digest)
            {
                builder.Append(value.ToString("x2"));
            }
            return builder.ToString();
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
            catch (MainThreadOutcomeUnknownException e)
            {
                response = CreateOutcomeUnknownEnvelope("execute", e, uuid);
            }
            catch (Exception e)
            {
                response = s_EnvelopeFactory.CreateTextEnvelope("execute", ConsoleLog.Format($"Execute exception: {e}"), uuid);
            }

            await WriteEnvelopeResponseAsync(context, response, "Execute");
        }
    }
}
