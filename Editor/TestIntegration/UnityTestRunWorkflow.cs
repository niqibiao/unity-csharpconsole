using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zh1Zh1.CSharpConsole.Service.Commands.Core;

namespace Zh1Zh1.CSharpConsole.Editor.TestIntegration
{
    /// <summary>
    /// Deep module for starting one Unity test run and tracking bounded,
    /// durable evidence across Play Mode and assembly reloads.
    /// </summary>
    internal static class UnityTestRunWorkflow
    {
        private const int SchemaVersion = 1;
        private const int MaxStateBytes = 64 * 1024;
        private const int MaxStatusJsonBytes = 16 * 1024;
        private const int MaxTestNames = 32;
        private const int MaxTestNameChars = 512;
        private const int MaxFailureDetails = 20;
        private const int MaxOperationMessageChars = 1024;
        private const int MaxFailureMessageChars = 1024;
        private const int MaxFailureStackChars = 2048;
        private const int MaxResultStateChars = 128;
        private const int MaxOutcomeChars = 32;
        private const int MaxHistoryRuns = 16;
        private const int ProgressPersistEvery = 25;
        private const double ProgressPersistSeconds = 1.0;
        private const double ReconcileIntervalSeconds = 0.5;
        private const double PlayModeOrphanGraceSeconds = 5.0;

        private const string RequestedPhase = "requested";
        private const string RunningPhase = "running";
        private const string CompletedPhase = "completed";
        private const string InterruptedPhase = "interrupted";

        private static readonly object s_Gate = new object();

        [Serializable]
        private sealed class FailureDetail
        {
            public string testName = "";
            public string resultState = "";
            public string message = "";
            public string stackTrace = "";
        }

        [Serializable]
        private sealed class TestRunState
        {
            public int schemaVersion = SchemaVersion;
            public int revision;
            public string runId = "";
            public string frameworkRunId = "";
            public bool ownershipConfirmed;
            public string phase = "";
            public string outcome = "";
            public string mode = "";
            public string[] testNames = Array.Empty<string>();
            public string requestedAtUtc = "";
            public string startedAtUtc = "";
            public string finishedAtUtc = "";
            public string updatedAtUtc = "";
            public int totalCount;
            public int completedCount;
            public int passedCount;
            public int failedCount;
            public int skippedCount;
            public int inconclusiveCount;
            public string currentTest = "";
            public string resultState = "";
            public double durationSeconds;
            public string message = "";
            public FailureDetail[] failureDetails = Array.Empty<FailureDetail>();
            public bool failuresTruncated;
        }

        [Serializable]
        private sealed class AcceptedResult
        {
            public string runId = "";
            public string phase = RequestedPhase;
            public string mode = "";
            public bool accepted;
        }

        [Serializable]
        private sealed class StatusResult
        {
            public string runId = "";
            public string phase = "";
            public string outcome = "";
            public string mode = "";
            public int totalCount;
            public int completedCount;
            public int passedCount;
            public int failedCount;
            public int skippedCount;
            public int inconclusiveCount;
            public string currentTest = "";
            public string resultState = "";
            public double durationSeconds;
            public string requestedAtUtc = "";
            public string startedAtUtc = "";
            public string finishedAtUtc = "";
            public string message = "";
            public FailureDetail[] failureDetails = Array.Empty<FailureDetail>();
            public int returnedFailureCount;
            public bool failuresTruncated;
        }

        private sealed class TestCallbacks : IErrorCallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) =>
                HandleRunStarted(testsToRun);

            public void RunFinished(ITestResultAdaptor result) =>
                HandleRunFinished(result);

            public void TestStarted(ITestAdaptor test) =>
                HandleTestStarted(test);

            public void TestFinished(ITestResultAdaptor result) =>
                HandleTestFinished(result);

            public void OnError(string message) =>
                HandleFrameworkError(message);
        }

        private static bool s_Initialized;
        private static bool s_PersistenceBlocked;
        private static int s_MainThreadId;
        private static string s_StatePath = "";
        private static string s_HistoryDirectory = "";
        private static string s_SeenDirectory = "";
        private static string s_LoadError = "";
        private static TestRunState s_State;
        private static TestCallbacks s_Callbacks;
        private static TestRunnerApi s_TestRunnerApi;
        private static double s_NextReconcileAt;
        private static double s_LastProgressPersistAt;
        private static double s_PlayModeOrphanedOutsidePlayAt = -1.0;

        [InitializeOnLoadMethod]
        private static void InitializeOnLoad()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess())
            {
                return;
            }

            InitializeOnMainThread();
        }

        internal static CommandResponse Run(
            CommandInvocation invocation,
            string mode,
            string[] testNames)
        {
            var normalizedRunId = NormalizeProtectedRunId(invocation);
            if (string.IsNullOrEmpty(normalizedRunId))
            {
                return CommandResponseFactory.ValidationError(
                    "tests/run requires a protected direct invocation id");
            }

            if (!TryNormalizeRequest(
                    mode,
                    testNames,
                    out var normalizedMode,
                    out var normalizedTestNames,
                    out var validationError))
            {
                return CommandResponseFactory.ValidationError(validationError);
            }

            if (HasDirtyLoadedScene())
            {
                return CommandResponseFactory.ValidationError(
                    "Save or discard all modified scenes before tests/run; non-interactive test execution cannot open the save prompt.");
            }

            if (!s_Initialized)
            {
                InitializeOnMainThread();
            }

            lock (s_Gate)
            {
                var availabilityError = GetAvailabilityErrorLocked();
                if (!string.IsNullOrEmpty(availabilityError))
                {
                    return SystemError(availabilityError);
                }

                if (IsActive(s_State))
                {
                    ReconcileActiveStateLocked("before accepting a new test run");
                    if (IsActive(s_State))
                    {
                        return CommandResponseFactory.ValidationError(
                            $"Unity tests run '{s_State.runId}' is still {s_State.phase}");
                    }

                    return CommandResponseFactory.ValidationError(
                        $"Previous Unity tests run '{s_State.runId}' became interrupted; inspect it with tests/status before starting a new run");
                }

                if (HasRunIdentityLocked(normalizedRunId))
                {
                    return CommandResponseFactory.ValidationError(
                        $"Unity tests run '{normalizedRunId}' was already accepted; query tests/status with the same runId");
                }

                var activeSnapshot = UnityTestFrameworkRunProbe.Capture();
                if (!activeSnapshot.available)
                {
                    return SystemError(activeSnapshot.error);
                }
                if (activeSnapshot.activeRunIds.Length > 0)
                {
                    return CommandResponseFactory.ValidationError(
                        "Another Unity Test Framework run is already active");
                }

                if (!TryArchiveTerminalCurrentLocked(out var archiveError))
                {
                    return SystemError(
                        $"Unity tests were not started because the previous run could not be archived safely: {archiveError}");
                }

                var now = UtcNow();
                var operation = new TestRunState
                {
                    runId = normalizedRunId,
                    phase = RequestedPhase,
                    mode = normalizedMode,
                    testNames = normalizedTestNames,
                    requestedAtUtc = now,
                    updatedAtUtc = now
                };

                if (!TryPublishStateLocked(operation, out var acceptanceError))
                {
                    return SystemError(
                        $"Unity tests were not started because durable acceptance failed: {acceptanceError}");
                }

                if (!TryCreateSeenMarkerLocked(normalizedRunId, out var markerError))
                {
                    InterruptLocked(
                        s_State,
                        $"The durable run identity marker could not be recorded: {markerError}");
                    return SystemError(
                        "Unity tests were not started because their runId could not be protected against redispatch");
                }

                if (!TryPruneHistoryLocked(out var pruneError))
                {
                    InterruptLocked(
                        s_State,
                        $"Retained Unity test history could not be bounded safely: {pruneError}");
                    return SystemError(
                        "Unity tests were not started because retained history could not be maintained safely");
                }

                string rawFrameworkRunId;
                try
                {
                    var filter = new Filter
                    {
                        testMode = string.Equals(
                            normalizedMode,
                            "edit",
                            StringComparison.Ordinal)
                            ? TestMode.EditMode
                            : TestMode.PlayMode
                    };
                    if (normalizedTestNames.Length > 0)
                    {
                        filter.testNames = (string[])normalizedTestNames.Clone();
                    }

                    rawFrameworkRunId = s_TestRunnerApi.Execute(
                        new ExecutionSettings(filter)
                        {
                            runSynchronously = false
                        });
                }
                catch (Exception e)
                {
                    InterruptLocked(
                        s_State,
                        $"Test Framework dispatch outcome is unknown: {e.GetType().Name}: {e.Message}");
                    return OutcomeUnknown(
                        s_State,
                        "Unity tests may have started, but Test Framework dispatch could not be confirmed");
                }

                if (!Guid.TryParse(rawFrameworkRunId, out var parsedFrameworkRunId))
                {
                    InterruptLocked(
                        s_State,
                        "Test Framework returned an invalid run id after dispatch");
                    return OutcomeUnknown(
                        s_State,
                        "Unity tests may have started, but the Test Framework run id is invalid");
                }

                var scheduled = CloneState(s_State);
                scheduled.frameworkRunId = parsedFrameworkRunId.ToString("D");
                if (!TryPublishStateLocked(scheduled, out var schedulePersistError))
                {
                    InterruptLocked(
                        scheduled,
                        $"Test Framework run id could not be persisted after dispatch: {schedulePersistError}");
                    return OutcomeUnknown(
                        s_State,
                        "Unity tests may have started, but their durable run identity could not be recorded");
                }

                var scheduledSnapshot = UnityTestFrameworkRunProbe.Capture();
                if (!OwnsOnlyActiveRun(s_State, scheduledSnapshot, out var ownershipError))
                {
                    InterruptLocked(s_State, ownershipError);
                    return OutcomeUnknown(
                        s_State,
                        "Unity tests were dispatched, but exclusive ownership of the run could not be proven");
                }

                var owned = CloneState(s_State);
                owned.ownershipConfirmed = true;
                if (!TryPublishStateLocked(owned, out var ownershipPersistError))
                {
                    InterruptLocked(
                        owned,
                        $"Test Framework ownership proof could not be persisted: {ownershipPersistError}");
                    return OutcomeUnknown(
                        s_State,
                        "Unity tests were dispatched, but their durable ownership proof could not be recorded");
                }

                s_PlayModeOrphanedOutsidePlayAt = -1.0;
                var accepted = new AcceptedResult
                {
                    runId = s_State.runId,
                    phase = s_State.phase,
                    mode = s_State.mode,
                    accepted = true
                };
                return CommandResponseFactory.Ok(
                    "Unity tests accepted",
                    JsonUtility.ToJson(accepted));
            }
        }

        internal static CommandResponse Status(string runId, int waitSeconds)
        {
            var normalizedRunId = (runId ?? "").Trim();
            if (!Guid.TryParseExact(normalizedRunId, "N", out var parsedRunId))
            {
                return CommandResponseFactory.ValidationError(
                    "runId must be the 32-character hexadecimal id returned by tests/run");
            }
            normalizedRunId = parsedRunId.ToString("N");
            if (waitSeconds < 0 || waitSeconds > 20)
            {
                return CommandResponseFactory.ValidationError(
                    "waitSeconds must be between 0 and 20");
            }
            if (!s_Initialized)
            {
                return SystemError(
                    "Unity test tracking is not initialized in this Editor domain");
            }

            TestRunState snapshot;
            lock (s_Gate)
            {
                if (!string.IsNullOrEmpty(s_LoadError))
                {
                    return SystemError(s_LoadError);
                }
                if (waitSeconds > 0
                    && IsActive(s_State)
                    && RunIdsEqual(s_State?.runId, normalizedRunId)
                    && Thread.CurrentThread.ManagedThreadId != s_MainThreadId)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
                    while (IsActive(s_State)
                        && RunIdsEqual(s_State?.runId, normalizedRunId))
                    {
                        var remaining = deadline - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero)
                        {
                            break;
                        }

                        Monitor.Wait(
                            s_Gate,
                            Math.Max(1, (int)Math.Min(
                                int.MaxValue,
                                remaining.TotalMilliseconds)));
                    }
                }

                snapshot = LoadRetainedRunLocked(
                    normalizedRunId,
                    out var retainedError);
                if (!string.IsNullOrEmpty(retainedError))
                {
                    return SystemError(retainedError);
                }
                if (snapshot == null)
                {
                    return CommandResponseFactory.ValidationError(
                        $"Unity tests run '{normalizedRunId}' is not retained");
                }
            }

            return BuildStatusResponse(snapshot);
        }

        private static void InitializeOnMainThread()
        {
            lock (s_Gate)
            {
                if (s_Initialized)
                {
                    return;
                }

                s_MainThreadId = Thread.CurrentThread.ManagedThreadId;
                s_StatePath = ResolveStatePath();
                var stateDirectory = Path.GetDirectoryName(s_StatePath) ?? "";
                s_HistoryDirectory = Path.Combine(
                    stateDirectory,
                    "history");
                s_SeenDirectory = Path.Combine(
                    stateDirectory,
                    "seen");
                s_State = LoadState(s_StatePath, out s_LoadError);
                s_PersistenceBlocked = !string.IsNullOrEmpty(s_LoadError);
                s_Callbacks = new TestCallbacks();
                s_TestRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
                s_TestRunnerApi.hideFlags = HideFlags.HideAndDontSave;
                s_TestRunnerApi.RegisterCallbacks(s_Callbacks, priority: 1000);
                s_NextReconcileAt =
                    EditorApplication.timeSinceStartup + ReconcileIntervalSeconds;
                s_LastProgressPersistAt = EditorApplication.timeSinceStartup;
                s_Initialized = true;

                if (IsActive(s_State)
                    && (
                        string.IsNullOrEmpty(s_State.frameworkRunId)
                        || !s_State.ownershipConfirmed
                    ))
                {
                    InterruptLocked(
                        s_State,
                        "Unity Editor reloaded before the Test Framework run identity and ownership proof were both durable");
                }
            }

            EditorApplication.update -= ReconcileOnEditorUpdate;
            EditorApplication.update += ReconcileOnEditorUpdate;
        }

        private static void ReconcileOnEditorUpdate()
        {
            if (!s_Initialized
                || EditorApplication.timeSinceStartup < s_NextReconcileAt)
            {
                return;
            }

            s_NextReconcileAt =
                EditorApplication.timeSinceStartup + ReconcileIntervalSeconds;
            lock (s_Gate)
            {
                if (IsActive(s_State))
                {
                    ReconcileActiveStateLocked("while tracking the active test run");
                }
            }
        }

        private static void ReconcileActiveStateLocked(string context)
        {
            if (!IsActive(s_State))
            {
                return;
            }
            if (string.IsNullOrEmpty(s_State.frameworkRunId))
            {
                InterruptLocked(
                    s_State,
                    $"Unity test evidence is incomplete {context}: no durable Test Framework run id");
                return;
            }

            var snapshot = UnityTestFrameworkRunProbe.Capture();
            if (OwnsOnlyActiveRun(s_State, snapshot, out _))
            {
                s_PlayModeOrphanedOutsidePlayAt = -1.0;
                return;
            }

            if (CanUseConfirmedPlayModeOwnership(
                    s_State,
                    snapshot,
                    out var ownershipError))
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    s_PlayModeOrphanedOutsidePlayAt = -1.0;
                    return;
                }

                if (s_PlayModeOrphanedOutsidePlayAt < 0.0)
                {
                    s_PlayModeOrphanedOutsidePlayAt =
                        EditorApplication.timeSinceStartup;
                    return;
                }
                if (EditorApplication.timeSinceStartup
                    - s_PlayModeOrphanedOutsidePlayAt
                    < PlayModeOrphanGraceSeconds)
                {
                    return;
                }

                ownershipError =
                    "The confirmed Play Mode callback stream ended without terminal evidence";
            }

            if (!string.IsNullOrEmpty(ownershipError))
            {
                InterruptLocked(s_State, $"{ownershipError} ({context})");
            }
        }

        private static void HandleRunStarted(ITestAdaptor testsToRun)
        {
            lock (s_Gate)
            {
                if (!CanAcceptCallbackLocked("run-start callback"))
                {
                    return;
                }

                var next = CloneState(s_State);
                next.phase = RunningPhase;
                next.startedAtUtc = string.IsNullOrEmpty(next.startedAtUtc)
                    ? UtcNow()
                    : next.startedAtUtc;
                var expectedTotal = next.testNames.Length > 0
                    ? next.testNames.Length
                    : CountLeafTests(testsToRun);
                next.totalCount = Math.Max(
                    next.totalCount,
                    expectedTotal);
                next.currentTest = "";
                if (!TryPublishStateLocked(next, out var persistError))
                {
                    InterruptLocked(
                        next,
                        $"Could not persist the Unity test run-start evidence: {persistError}");
                }
            }
        }

        private static void HandleTestStarted(ITestAdaptor test)
        {
            if (test == null || test.HasChildren)
            {
                return;
            }

            lock (s_Gate)
            {
                if (!CanAcceptCallbackLocked("test-start callback"))
                {
                    return;
                }

                var next = CloneState(s_State);
                next.phase = RunningPhase;
                next.startedAtUtc = string.IsNullOrEmpty(next.startedAtUtc)
                    ? UtcNow()
                    : next.startedAtUtc;
                next.currentTest = Truncate(
                    string.IsNullOrEmpty(test.FullName)
                        ? test.UniqueName
                        : test.FullName,
                    MaxTestNameChars);
                PublishVolatileLocked(next);
            }
        }

        private static int CountLeafTests(ITestAdaptor root)
        {
            if (root == null)
            {
                return 0;
            }

            var count = 0;
            var pending = new Stack<ITestAdaptor>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current == null)
                {
                    continue;
                }
                if (!current.HasChildren)
                {
                    if (!current.IsSuite)
                    {
                        count++;
                    }
                    continue;
                }
                if (current.Children == null)
                {
                    continue;
                }

                foreach (var child in current.Children)
                {
                    if (child != null)
                    {
                        pending.Push(child);
                    }
                }
            }

            return count;
        }

        private static void HandleTestFinished(ITestResultAdaptor result)
        {
            if (result == null || result.HasChildren)
            {
                return;
            }

            lock (s_Gate)
            {
                if (!CanAcceptCallbackLocked("test-finish callback"))
                {
                    return;
                }

                var next = CloneState(s_State);
                next.phase = RunningPhase;
                next.startedAtUtc = string.IsNullOrEmpty(next.startedAtUtc)
                    ? UtcNow()
                    : next.startedAtUtc;
                next.currentTest = "";

                var status = result.TestStatus.ToString();
                switch (status)
                {
                    case "Passed":
                        next.passedCount++;
                        break;
                    case "Failed":
                        next.failedCount++;
                        AddFailureDetail(next, result);
                        break;
                    case "Skipped":
                        next.skippedCount++;
                        break;
                    default:
                        next.inconclusiveCount++;
                        break;
                }

                var observedCompleted =
                    next.passedCount
                    + next.failedCount
                    + next.skippedCount
                    + next.inconclusiveCount;
                next.completedCount = next.totalCount > 0
                    ? Math.Min(next.totalCount, observedCompleted)
                    : observedCompleted;

                var shouldPersist =
                    string.Equals(status, "Failed", StringComparison.Ordinal)
                    || next.completedCount % ProgressPersistEvery == 0
                    || (
                        EditorApplication.timeSinceStartup
                        - s_LastProgressPersistAt
                    ) >= ProgressPersistSeconds;
                if (!shouldPersist)
                {
                    PublishVolatileLocked(next);
                    return;
                }

                if (!TryPublishStateLocked(next, out var persistError))
                {
                    InterruptLocked(
                        next,
                        $"Could not persist Unity test progress evidence: {persistError}");
                    return;
                }

                s_LastProgressPersistAt = EditorApplication.timeSinceStartup;
            }
        }

        private static void HandleRunFinished(ITestResultAdaptor result)
        {
            lock (s_Gate)
            {
                if (!CanAcceptCallbackLocked("run-finish callback"))
                {
                    return;
                }

                if (result == null)
                {
                    InterruptLocked(
                        s_State,
                        "Test Framework returned no root result; terminal evidence could not be proven");
                    return;
                }

                var next = CloneState(s_State);
                next.phase = CompletedPhase;
                next.finishedAtUtc = UtcNow();
                next.currentTest = "";
                next.resultState = Truncate(
                    result.ResultState,
                    MaxResultStateChars);
                next.durationSeconds = Math.Max(0.0, result.Duration);
                next.passedCount = Math.Max(0, result.PassCount);
                next.failedCount = Math.Max(0, result.FailCount);
                next.skippedCount = Math.Max(0, result.SkipCount);
                next.inconclusiveCount = Math.Max(
                    0,
                    result.InconclusiveCount);
                next.completedCount =
                    next.passedCount
                    + next.failedCount
                    + next.skippedCount
                    + next.inconclusiveCount;
                next.totalCount = next.completedCount;
                CollectFailureDetails(next, result, 0);
                next.outcome = DetermineOutcome(next, result);
                next.message = Truncate(
                    result.Message,
                    MaxOperationMessageChars);
                next.failuresTruncated =
                    next.failuresTruncated
                    || next.failedCount > (next.failureDetails?.Length ?? 0);

                if (!TryPublishStateLocked(next, out var persistError))
                {
                    InterruptLocked(
                        next,
                        $"Could not persist terminal Unity test evidence: {persistError}");
                }
            }
        }

        private static void HandleFrameworkError(string message)
        {
            lock (s_Gate)
            {
                if (!IsActive(s_State))
                {
                    return;
                }

                // IErrorCallbacks carries no run id, and the failing framework
                // job may already have been removed before this callback.
                // Preserve the diagnostic but never attribute it as a proven
                // terminal result.
                InterruptLocked(
                    s_State,
                    $"Test Framework reported an uncorrelated error: {Truncate(message, MaxOperationMessageChars)}");
            }
        }

        private static bool CanAcceptCallbackLocked(string callbackName)
        {
            if (!IsActive(s_State))
            {
                return false;
            }

            var snapshot = UnityTestFrameworkRunProbe.Capture();
            if (OwnsOnlyActiveRun(s_State, snapshot, out var ownershipError)
                || CanUseConfirmedPlayModeOwnership(
                    s_State,
                    snapshot,
                    out ownershipError))
            {
                return true;
            }

            InterruptLocked(
                s_State,
                $"{ownershipError}; ignored {callbackName}");
            return false;
        }

        private static bool OwnsOnlyActiveRun(
            TestRunState state,
            UnityTestFrameworkRunProbe.Snapshot snapshot,
            out string error)
        {
            error = "";
            if (snapshot == null || !snapshot.available)
            {
                error = snapshot?.error
                    ?? "Test Framework run ownership probe is unavailable";
                return false;
            }
            if (state == null
                || !Guid.TryParse(state.frameworkRunId, out var expectedRunId))
            {
                error = "Durable Test Framework run id is unavailable";
                return false;
            }
            if (snapshot.activeRunIds.Length != 1)
            {
                error = snapshot.activeRunIds.Length == 0
                    ? "The tracked Test Framework run is no longer active and no terminal callback was proven"
                    : "Multiple Test Framework runs are active, so callback ownership is ambiguous";
                return false;
            }
            if (!Guid.TryParse(snapshot.activeRunIds[0], out var activeRunId)
                || activeRunId != expectedRunId)
            {
                error =
                    "The active Test Framework run does not match the durable run id";
                return false;
            }

            return true;
        }

        private static bool CanUseConfirmedPlayModeOwnership(
            TestRunState state,
            UnityTestFrameworkRunProbe.Snapshot snapshot,
            out string error)
        {
            error = "";
            if (snapshot == null || !snapshot.available)
            {
                error = snapshot?.error
                    ?? "Test Framework run ownership probe is unavailable";
                return false;
            }
            if (state == null
                || !state.ownershipConfirmed
                || !string.Equals(state.mode, "play", StringComparison.Ordinal))
            {
                error = "The tracked Test Framework run is no longer active and no terminal callback was proven";
                return false;
            }
            if (snapshot.activeRunIds.Length > 0)
            {
                error =
                    "A different Test Framework run is active, so callback ownership is ambiguous";
                return false;
            }

            // Entering Play Mode can end the editor-side TestJobData before
            // remote callbacks arrive. Initial exclusive ownership was already
            // proven and persisted; accept the callback stream only while no
            // conflicting framework job exists.
            return true;
        }

        private static void AddFailureDetail(
            TestRunState state,
            ITestResultAdaptor result)
        {
            var details = state.failureDetails ?? Array.Empty<FailureDetail>();
            var testName = Truncate(
                string.IsNullOrEmpty(result.FullName)
                    ? result.Test?.UniqueName
                    : result.FullName,
                MaxTestNameChars);
            var resultState = Truncate(
                result.ResultState,
                MaxResultStateChars);
            if (details.Any(detail =>
                    string.Equals(
                        detail?.testName,
                        testName,
                        StringComparison.Ordinal)
                    && string.Equals(
                        detail?.resultState,
                        resultState,
                        StringComparison.Ordinal)))
            {
                return;
            }
            if (details.Length >= MaxFailureDetails)
            {
                state.failuresTruncated = true;
                return;
            }

            var candidate = new FailureDetail[details.Length + 1];
            Array.Copy(details, candidate, details.Length);
            candidate[candidate.Length - 1] = new FailureDetail
            {
                testName = testName,
                resultState = resultState,
                message = Truncate(
                    result.Message,
                    MaxFailureMessageChars),
                stackTrace = Truncate(
                    result.StackTrace,
                    MaxFailureStackChars)
            };
            state.failureDetails = candidate;

            if (Encoding.UTF8.GetByteCount(JsonUtility.ToJson(state))
                > MaxStateBytes - 4096)
            {
                state.failureDetails = details;
                state.failuresTruncated = true;
            }
        }

        private static void CollectFailureDetails(
            TestRunState state,
            ITestResultAdaptor result,
            int depth)
        {
            if (state == null || result == null)
            {
                return;
            }
            if (depth > 256)
            {
                state.failuresTruncated = true;
                return;
            }

            var resultState = result.ResultState ?? "";
            var failed =
                string.Equals(
                    result.TestStatus.ToString(),
                    "Failed",
                    StringComparison.Ordinal)
                || resultState.StartsWith(
                    "Failed",
                    StringComparison.Ordinal);
            var aggregateOnly =
                result.HasChildren
                && string.Equals(
                    resultState,
                    "Failed(Child)",
                    StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(result.StackTrace);
            if (failed && !aggregateOnly)
            {
                AddFailureDetail(state, result);
            }

            if (!result.HasChildren || result.Children == null)
            {
                return;
            }

            foreach (var child in result.Children)
            {
                CollectFailureDetails(state, child, depth + 1);
            }
        }

        private static CommandResponse BuildStatusResponse(TestRunState state)
        {
            var result = new StatusResult
            {
                runId = state.runId,
                phase = state.phase,
                outcome = state.outcome,
                mode = state.mode,
                totalCount = state.totalCount,
                completedCount = state.completedCount,
                passedCount = state.passedCount,
                failedCount = state.failedCount,
                skippedCount = state.skippedCount,
                inconclusiveCount = state.inconclusiveCount,
                currentTest = state.currentTest,
                resultState = state.resultState,
                durationSeconds = state.durationSeconds,
                requestedAtUtc = state.requestedAtUtc,
                startedAtUtc = state.startedAtUtc,
                finishedAtUtc = state.finishedAtUtc,
                message = state.message,
                failureDetails = CloneFailures(state.failureDetails),
                failuresTruncated = state.failuresTruncated
            };

            var originalFailureCount = result.failureDetails.Length;
            var json = JsonUtility.ToJson(result);
            while (Encoding.UTF8.GetByteCount(json) > MaxStatusJsonBytes
                && result.failureDetails.Length > 0)
            {
                Array.Resize(
                    ref result.failureDetails,
                    result.failureDetails.Length - 1);
                result.failuresTruncated = true;
                json = JsonUtility.ToJson(result);
            }

            result.returnedFailureCount = result.failureDetails.Length;
            result.failuresTruncated =
                result.failuresTruncated
                || result.failureDetails.Length < originalFailureCount;
            json = JsonUtility.ToJson(result);
            if (Encoding.UTF8.GetByteCount(json) > MaxStatusJsonBytes)
            {
                result.currentTest = Truncate(result.currentTest, 128);
                result.message = Truncate(result.message, 256);
                result.failureDetails = Array.Empty<FailureDetail>();
                result.returnedFailureCount = 0;
                result.failuresTruncated = true;
                json = JsonUtility.ToJson(result);
            }
            if (Encoding.UTF8.GetByteCount(json) > MaxStatusJsonBytes)
            {
                return SystemError(
                    "Unity test status exceeded the 16 KiB response limit and was withheld");
            }

            var summary = state.phase switch
            {
                CompletedPhase =>
                    $"Unity tests completed with {state.failedCount} failed",
                InterruptedPhase =>
                    "Unity test evidence is interrupted",
                RunningPhase =>
                    $"Unity tests running: {state.completedCount}/{state.totalCount}",
                _ => "Unity tests requested"
            };
            return CommandResponseFactory.Ok(summary, json);
        }

        private static string DetermineOutcome(
            TestRunState state,
            ITestResultAdaptor rootResult)
        {
            var rootStatus = rootResult?.TestStatus.ToString() ?? "";
            var rootResultState = rootResult?.ResultState ?? "";
            if (string.Equals(
                    rootStatus,
                    "Failed",
                    StringComparison.Ordinal)
                || rootResultState.StartsWith(
                    "Failed",
                    StringComparison.Ordinal))
            {
                return "failed";
            }
            if (string.Equals(
                    rootStatus,
                    "Inconclusive",
                    StringComparison.Ordinal))
            {
                return "inconclusive";
            }
            if (string.Equals(
                    rootStatus,
                    "Skipped",
                    StringComparison.Ordinal))
            {
                return "skipped";
            }
            if (!string.Equals(
                    rootStatus,
                    "Passed",
                    StringComparison.Ordinal))
            {
                return "unknown";
            }

            if (state.failedCount > 0)
            {
                return "failed";
            }
            if (state.inconclusiveCount > 0)
            {
                return "inconclusive";
            }
            if (state.totalCount == 0 && state.completedCount == 0)
            {
                return "no_tests";
            }
            if (state.passedCount > 0)
            {
                return "passed";
            }
            if (state.skippedCount > 0)
            {
                return "skipped";
            }

            return "unknown";
        }

        private static bool TryNormalizeRequest(
            string mode,
            string[] testNames,
            out string normalizedMode,
            out string[] normalizedTestNames,
            out string error)
        {
            normalizedMode = (mode ?? "").Trim().ToLowerInvariant();
            normalizedTestNames = Array.Empty<string>();
            error = "";
            if (normalizedMode != "edit" && normalizedMode != "play")
            {
                error = "mode must be 'edit' or 'play'";
                return false;
            }
            if (testNames == null)
            {
                return true;
            }
            if (testNames.Length == 0)
            {
                error = "testNames must be omitted or contain at least one exact test name";
                return false;
            }
            if (testNames.Length > MaxTestNames)
            {
                error = $"testNames must not contain more than {MaxTestNames} entries";
                return false;
            }

            var normalized = new List<string>(testNames.Length);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < testNames.Length; index++)
            {
                var name = (testNames[index] ?? "").Trim();
                if (string.IsNullOrEmpty(name))
                {
                    error = $"testNames[{index}] must be non-empty";
                    return false;
                }
                if (name.Length > MaxTestNameChars)
                {
                    error =
                        $"testNames[{index}] must not exceed {MaxTestNameChars} characters";
                    return false;
                }
                if (seen.Add(name))
                {
                    normalized.Add(name);
                }
            }

            normalizedTestNames = normalized.ToArray();
            return true;
        }

        private static string GetAvailabilityErrorLocked()
        {
            if (!s_Initialized)
            {
                return "Unity test tracking is not initialized";
            }
            if (!string.IsNullOrEmpty(s_LoadError))
            {
                return s_LoadError;
            }
            if (s_PersistenceBlocked)
            {
                return "Unity test state persistence is unavailable; no new run can be accepted safely";
            }
            if (s_TestRunnerApi == null)
            {
                return "Unity Test Framework API is unavailable";
            }

            return "";
        }

        private static string NormalizeProtectedRunId(
            CommandInvocation invocation)
        {
            return Guid.TryParse(
                invocation?.protectedInvocationId?.Trim(),
                out var parsed)
                ? parsed.ToString("N")
                : "";
        }

        private static bool HasDirtyLoadedScene()
        {
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                if (SceneManager.GetSceneAt(index).isDirty)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasRunIdentityLocked(string runId)
        {
            if (RunIdsEqual(s_State?.runId, runId))
            {
                return true;
            }

            return File.Exists(GetHistoryPath(runId))
                || File.Exists(GetSeenPath(runId));
        }

        private static bool TryArchiveTerminalCurrentLocked(out string error)
        {
            error = "";
            if (s_State == null)
            {
                return true;
            }
            if (!IsTerminal(s_State.phase))
            {
                error = "the current run is not terminal";
                return false;
            }
            if (!TryCreateSeenMarkerLocked(s_State.runId, out error))
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(s_HistoryDirectory);
                var path = GetHistoryPath(s_State.runId);
                if (File.Exists(path))
                {
                    var existing = TryReadStateDocument(
                        path,
                        out var existingError);
                    if (existing == null
                        || !RunIdsEqual(existing.runId, s_State.runId)
                        || !IsTerminal(existing.phase))
                    {
                        error =
                            "an existing history record is unreadable or does not match its runId: "
                            + existingError;
                        return false;
                    }

                    return true;
                }

                WriteStateDurably(
                    path,
                    JsonUtility.ToJson(CloneState(s_State)));
                var archived = TryReadStateDocument(
                    path,
                    out var archiveReadError);
                if (archived == null
                    || !RunIdsEqual(archived.runId, s_State.runId)
                    || !IsTerminal(archived.phase))
                {
                    error =
                        "the archived history record could not be verified: "
                        + archiveReadError;
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private static bool TryCreateSeenMarkerLocked(
            string runId,
            out string error)
        {
            error = "";
            if (!Guid.TryParseExact(runId, "N", out _))
            {
                error = "runId is invalid";
                return false;
            }

            try
            {
                Directory.CreateDirectory(s_SeenDirectory);
                var path = GetSeenPath(runId);
                if (File.Exists(path))
                {
                    return true;
                }

                var bytes = new UTF8Encoding(false).GetBytes(runId);
                using var stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
                return true;
            }
            catch (IOException)
            {
                if (File.Exists(GetSeenPath(runId)))
                {
                    return true;
                }

                error = "runId marker could not be created";
                return false;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private static bool TryPruneHistoryLocked(out string error)
        {
            error = "";
            try
            {
                if (!Directory.Exists(s_HistoryDirectory))
                {
                    return true;
                }

                var paths = Directory
                    .GetFiles(s_HistoryDirectory, "*.json")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ThenBy(path => path, StringComparer.Ordinal)
                    .ToArray();
                var retainedRunIds = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(s_State?.runId))
                {
                    retainedRunIds.Add(s_State.runId);
                }
                for (var index = 0;
                    index < Math.Min(MaxHistoryRuns, paths.Length);
                    index++)
                {
                    var retainedName =
                        Path.GetFileNameWithoutExtension(paths[index]);
                    if (Guid.TryParseExact(retainedName, "N", out _))
                    {
                        retainedRunIds.Add(retainedName);
                    }
                }

                for (var index = MaxHistoryRuns; index < paths.Length; index++)
                {
                    var path = paths[index];
                    var state = TryReadStateDocument(path, out var readError);
                    if (state == null
                        || !IsTerminal(state.phase)
                        || !string.Equals(
                            Path.GetFileName(path),
                            state.runId + ".json",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        error =
                            $"history record '{Path.GetFileName(path)}' is unreadable or mismatched: {readError}";
                        return false;
                    }
                    if (!TryCreateSeenMarkerLocked(state.runId, out error))
                    {
                        return false;
                    }

                    var markerPath = GetSeenPath(state.runId);
                    if (File.Exists(markerPath))
                    {
                        File.Delete(markerPath);
                    }
                    File.Delete(path);
                    DeleteBackup(path);
                    DeleteStaleTemps(path);
                }

                if (Directory.Exists(s_SeenDirectory))
                {
                    foreach (var markerPath in Directory.GetFiles(
                        s_SeenDirectory,
                        "*.seen"))
                    {
                        var markerRunId =
                            Path.GetFileNameWithoutExtension(markerPath);
                        if (!retainedRunIds.Contains(markerRunId))
                        {
                            File.Delete(markerPath);
                        }
                    }
                }

                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private static TestRunState LoadRetainedRunLocked(
            string runId,
            out string error)
        {
            error = "";
            if (RunIdsEqual(s_State?.runId, runId))
            {
                return CloneState(s_State);
            }

            var path = GetHistoryPath(runId);
            var hasRecord =
                File.Exists(path)
                || File.Exists(path + ".backup");
            if (!hasRecord)
            {
                return null;
            }

            var state = LoadState(path, out error);
            if (state == null)
            {
                if (string.IsNullOrEmpty(error))
                {
                    error =
                        $"Unity test history for run '{runId}' disappeared while it was being read";
                }
                return null;
            }
            if (!RunIdsEqual(state.runId, runId)
                || !IsTerminal(state.phase))
            {
                error =
                    $"Unity test history for run '{runId}' is mismatched or non-terminal";
                return null;
            }

            return state;
        }

        private static string GetHistoryPath(string runId)
        {
            return Path.Combine(
                s_HistoryDirectory ?? "",
                (runId ?? "") + ".json");
        }

        private static string GetSeenPath(string runId)
        {
            return Path.Combine(
                s_SeenDirectory ?? "",
                (runId ?? "") + ".seen");
        }

        private static bool RunIdsEqual(string left, string right)
        {
            return string.Equals(
                left ?? "",
                right ?? "",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryPublishStateLocked(
            TestRunState state,
            out string error)
        {
            error = "";
            var persisted = CloneState(state);
            persisted.schemaVersion = SchemaVersion;
            persisted.revision = Math.Max(
                persisted.revision,
                s_State?.revision ?? 0) + 1;
            persisted.updatedAtUtc = UtcNow();

            try
            {
                WriteStateDurably(s_StatePath, JsonUtility.ToJson(persisted));
                s_State = CloneState(persisted);
                s_PersistenceBlocked = false;
                Monitor.PulseAll(s_Gate);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private static void PublishVolatileLocked(TestRunState state)
        {
            state.updatedAtUtc = UtcNow();
            s_State = CloneState(state);
            Monitor.PulseAll(s_Gate);
        }

        private static void InterruptLocked(TestRunState basis, string message)
        {
            var interrupted = CloneState(basis ?? s_State ?? new TestRunState());
            interrupted.phase = InterruptedPhase;
            interrupted.outcome = "unknown";
            interrupted.finishedAtUtc = UtcNow();
            interrupted.currentTest = "";
            interrupted.message = Truncate(
                message,
                MaxOperationMessageChars);
            interrupted.updatedAtUtc = UtcNow();
            interrupted.revision = Math.Max(
                interrupted.revision,
                s_State?.revision ?? 0) + 1;

            try
            {
                WriteStateDurably(
                    s_StatePath,
                    JsonUtility.ToJson(interrupted));
                s_PersistenceBlocked = false;
            }
            catch
            {
                s_PersistenceBlocked = true;
            }

            s_State = CloneState(interrupted);
            Monitor.PulseAll(s_Gate);
        }

        private static CommandResponse OutcomeUnknown(
            TestRunState state,
            string summary)
        {
            return new CommandResponse
            {
                ok = false,
                type = "outcome_unknown",
                summary = summary ?? "Unity test outcome is unknown",
                resultJson = JsonUtility.ToJson(new AcceptedResult
                {
                    runId = state?.runId ?? "",
                    phase = state?.phase ?? InterruptedPhase,
                    mode = state?.mode ?? "",
                    accepted = false
                })
            };
        }

        private static CommandResponse SystemError(string summary)
        {
            return new CommandResponse
            {
                ok = false,
                type = "system_error",
                summary = summary ?? "Unity test state is unavailable",
                resultJson = "{}"
            };
        }

        private static bool IsActive(TestRunState state)
        {
            return state != null
                && (
                    string.Equals(
                        state.phase,
                        RequestedPhase,
                        StringComparison.Ordinal)
                    || string.Equals(
                        state.phase,
                        RunningPhase,
                        StringComparison.Ordinal)
                );
        }

        private static bool IsTerminal(string phase)
        {
            return string.Equals(
                    phase,
                    CompletedPhase,
                    StringComparison.Ordinal)
                || string.Equals(
                    phase,
                    InterruptedPhase,
                    StringComparison.Ordinal);
        }

        private static TestRunState LoadState(
            string path,
            out string error)
        {
            error = "";
            try
            {
                RecoverMissingCanonical(path);
                if (!File.Exists(path))
                {
                    DeleteStaleTemps(path);
                    return null;
                }

                var canonical = TryReadStateDocument(path, out var canonicalError);
                if (canonical != null)
                {
                    DeleteBackup(path);
                    DeleteStaleTemps(path);
                    return canonical;
                }

                var backupPath = path + ".backup";
                var backup = File.Exists(backupPath)
                    ? TryReadStateDocument(backupPath, out _)
                    : null;
                if (backup != null)
                {
                    WriteStateDurably(path, JsonUtility.ToJson(backup));
                    DeleteBackup(path);
                    DeleteStaleTemps(path);
                    return backup;
                }

                error =
                    "Unity test state is unreadable; its previous outcome is unknown: "
                    + canonicalError;
                return null;
            }
            catch (Exception e)
            {
                error =
                    $"Unity test state could not be read; its previous outcome is unknown: {e.Message}";
                return null;
            }
        }

        private static TestRunState TryReadStateDocument(
            string path,
            out string error)
        {
            error = "";
            try
            {
                if (new FileInfo(path).Length > MaxStateBytes)
                {
                    error = "state file exceeds the 64 KiB safety limit";
                    return null;
                }

                var json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    error = "state file is empty";
                    return null;
                }

                var state = JsonUtility.FromJson<TestRunState>(json);
                if (!TryValidateStateDocument(json, state, out error))
                {
                    return null;
                }

                return CloneState(state);
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        private static bool TryValidateStateDocument(
            string json,
            TestRunState state,
            out string error)
        {
            error = "";
            var requiredFields = new[]
            {
                "schemaVersion",
                "revision",
                "runId",
                "frameworkRunId",
                "ownershipConfirmed",
                "phase",
                "outcome",
                "mode",
                "testNames",
                "requestedAtUtc",
                "startedAtUtc",
                "finishedAtUtc",
                "updatedAtUtc",
                "totalCount",
                "completedCount",
                "passedCount",
                "failedCount",
                "skippedCount",
                "inconclusiveCount",
                "currentTest",
                "resultState",
                "durationSeconds",
                "message",
                "failureDetails",
                "failuresTruncated"
            };
            foreach (var field in requiredFields)
            {
                if (!ContainsJsonProperty(json, field))
                {
                    error = $"required field '{field}' is missing";
                    return false;
                }
            }

            if (state == null || state.schemaVersion != SchemaVersion)
            {
                error = "schemaVersion is unsupported";
                return false;
            }
            if (state.revision <= 0)
            {
                error = "revision must be positive";
                return false;
            }
            if (!Guid.TryParseExact(state.runId, "N", out _))
            {
                error = "runId is invalid";
                return false;
            }
            if (!string.IsNullOrEmpty(state.frameworkRunId)
                && !Guid.TryParse(state.frameworkRunId, out _))
            {
                error = "frameworkRunId is invalid";
                return false;
            }
            if (!string.Equals(state.phase, RequestedPhase, StringComparison.Ordinal)
                && !string.Equals(state.phase, RunningPhase, StringComparison.Ordinal)
                && !string.Equals(state.phase, CompletedPhase, StringComparison.Ordinal)
                && !string.Equals(state.phase, InterruptedPhase, StringComparison.Ordinal))
            {
                error = $"phase '{state.phase}' is not recognized";
                return false;
            }
            if (state.mode != "edit" && state.mode != "play")
            {
                error = "mode is invalid";
                return false;
            }
            if ((state.outcome?.Length ?? 0) > MaxOutcomeChars
                || (
                    !string.IsNullOrEmpty(state.outcome)
                    && state.outcome != "passed"
                    && state.outcome != "failed"
                    && state.outcome != "skipped"
                    && state.outcome != "inconclusive"
                    && state.outcome != "no_tests"
                    && state.outcome != "unknown"
                ))
            {
                error = "outcome is invalid";
                return false;
            }
            if (IsActive(state) && !string.IsNullOrEmpty(state.outcome))
            {
                error = "active state must not have a terminal outcome";
                return false;
            }
            if (string.Equals(
                    state.phase,
                    CompletedPhase,
                    StringComparison.Ordinal)
                && string.IsNullOrEmpty(state.outcome))
            {
                error = "completed state requires an outcome";
                return false;
            }
            if (string.Equals(
                    state.phase,
                    InterruptedPhase,
                    StringComparison.Ordinal)
                && !string.Equals(
                    state.outcome,
                    "unknown",
                    StringComparison.Ordinal))
            {
                error = "interrupted state requires outcome 'unknown'";
                return false;
            }
            if (state.testNames == null
                || state.testNames.Length > MaxTestNames
                || state.testNames.Any(name =>
                    string.IsNullOrWhiteSpace(name)
                    || name.Length > MaxTestNameChars))
            {
                error = "testNames is invalid";
                return false;
            }
            if (!IsIsoTimestamp(state.requestedAtUtc)
                || !IsIsoTimestamp(state.updatedAtUtc)
                || (
                    !string.IsNullOrEmpty(state.startedAtUtc)
                    && !IsIsoTimestamp(state.startedAtUtc)
                )
                || (
                    !string.IsNullOrEmpty(state.finishedAtUtc)
                    && !IsIsoTimestamp(state.finishedAtUtc)
                ))
            {
                error = "one or more timestamps are invalid";
                return false;
            }
            if (string.Equals(state.phase, RunningPhase, StringComparison.Ordinal)
                && string.IsNullOrEmpty(state.frameworkRunId))
            {
                error = "running state requires frameworkRunId";
                return false;
            }
            if (string.Equals(state.phase, RunningPhase, StringComparison.Ordinal)
                && !state.ownershipConfirmed)
            {
                error = "running state requires durable ownership proof";
                return false;
            }
            if (IsTerminal(state.phase)
                && string.IsNullOrEmpty(state.finishedAtUtc))
            {
                error = "terminal state requires finishedAtUtc";
                return false;
            }
            if (state.totalCount < 0
                || state.completedCount < 0
                || state.passedCount < 0
                || state.failedCount < 0
                || state.skippedCount < 0
                || state.inconclusiveCount < 0
                || state.durationSeconds < 0)
            {
                error = "counts and duration must be non-negative";
                return false;
            }
            if (state.failureDetails == null
                || state.failureDetails.Length > MaxFailureDetails
                || state.failureDetails.Any(detail =>
                    detail == null
                    || (detail.testName?.Length ?? 0) > MaxTestNameChars
                    || (detail.resultState?.Length ?? 0) > MaxResultStateChars
                    || (detail.message?.Length ?? 0) > MaxFailureMessageChars
                    || (detail.stackTrace?.Length ?? 0) > MaxFailureStackChars))
            {
                error = "failureDetails is invalid";
                return false;
            }
            if ((state.message?.Length ?? 0) > MaxOperationMessageChars
                || (state.currentTest?.Length ?? 0) > MaxTestNameChars
                || (state.resultState?.Length ?? 0) > MaxResultStateChars)
            {
                error = "bounded state strings exceed their limits";
                return false;
            }

            return true;
        }

        private static void WriteStateDurably(string path, string json)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new IOException("Unity test state path is unavailable");
            }

            var bytes = new UTF8Encoding(false).GetBytes(json ?? "");
            if (bytes.Length > MaxStateBytes)
            {
                throw new IOException("Unity test state exceeds the 64 KiB safety limit");
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = path + $".tmp.{Guid.NewGuid():N}";
            try
            {
                using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
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
                        ReplaceWithBackup(path, tempPath);
                    }
                    catch (NotSupportedException)
                    {
                        ReplaceWithBackup(path, tempPath);
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
                        // Canonical state is either durable or the caller fails
                        // closed. Startup removes leftover temp files.
                    }
                }
            }
        }

        private static void ReplaceWithBackup(
            string path,
            string tempPath)
        {
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
            catch
            {
                // The new canonical is complete. Startup discards the older
                // backup after validating the canonical document.
            }
        }

        private static void RecoverMissingCanonical(string path)
        {
            var backupPath = path + ".backup";
            if (!File.Exists(path) && File.Exists(backupPath))
            {
                File.Move(backupPath, path);
            }
        }

        private static void DeleteBackup(string path)
        {
            var backupPath = path + ".backup";
            if (File.Exists(backupPath))
            {
                try
                {
                    File.Delete(backupPath);
                }
                catch
                {
                    // A valid canonical remains authoritative.
                }
            }
        }

        private static void DeleteStaleTemps(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            foreach (var stalePath in Directory.GetFiles(
                directory,
                Path.GetFileName(path) + ".tmp.*"))
            {
                try
                {
                    File.Delete(stalePath);
                }
                catch
                {
                    // Best effort only; temp files are never authoritative.
                }
            }
        }

        private static string ResolveStatePath()
        {
            return Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "..",
                "Library",
                "CSharpConsole",
                "TestRuns",
                "v1",
                "state.json"));
        }

        private static TestRunState CloneState(TestRunState source)
        {
            if (source == null)
            {
                return null;
            }

            return new TestRunState
            {
                schemaVersion = source.schemaVersion,
                revision = source.revision,
                runId = source.runId ?? "",
                frameworkRunId = source.frameworkRunId ?? "",
                ownershipConfirmed = source.ownershipConfirmed,
                phase = source.phase ?? "",
                outcome = source.outcome ?? "",
                mode = source.mode ?? "",
                testNames = source.testNames == null
                    ? Array.Empty<string>()
                    : (string[])source.testNames.Clone(),
                requestedAtUtc = source.requestedAtUtc ?? "",
                startedAtUtc = source.startedAtUtc ?? "",
                finishedAtUtc = source.finishedAtUtc ?? "",
                updatedAtUtc = source.updatedAtUtc ?? "",
                totalCount = source.totalCount,
                completedCount = source.completedCount,
                passedCount = source.passedCount,
                failedCount = source.failedCount,
                skippedCount = source.skippedCount,
                inconclusiveCount = source.inconclusiveCount,
                currentTest = source.currentTest ?? "",
                resultState = source.resultState ?? "",
                durationSeconds = source.durationSeconds,
                message = source.message ?? "",
                failureDetails = CloneFailures(source.failureDetails),
                failuresTruncated = source.failuresTruncated
            };
        }

        private static FailureDetail[] CloneFailures(
            FailureDetail[] failures)
        {
            if (failures == null || failures.Length == 0)
            {
                return Array.Empty<FailureDetail>();
            }

            var clone = new FailureDetail[failures.Length];
            for (var index = 0; index < failures.Length; index++)
            {
                var source = failures[index] ?? new FailureDetail();
                clone[index] = new FailureDetail
                {
                    testName = source.testName ?? "",
                    resultState = source.resultState ?? "",
                    message = source.message ?? "",
                    stackTrace = source.stackTrace ?? ""
                };
            }

            return clone;
        }

        private static bool ContainsJsonProperty(
            string json,
            string propertyName)
        {
            var marker = $"\"{propertyName}\"";
            var index = 0;
            while ((index = json.IndexOf(
                    marker,
                    index,
                    StringComparison.Ordinal)) >= 0)
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

        private static bool IsIsoTimestamp(string value)
        {
            return DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _);
        }

        private static string UtcNow()
        {
            return DateTime.UtcNow.ToString(
                "O",
                CultureInfo.InvariantCulture);
        }

        private static string Truncate(string value, int maxChars)
        {
            var text = value ?? "";
            if (text.Length <= maxChars)
            {
                return text;
            }

            var length = maxChars;
            if (length > 0
                && length < text.Length
                && char.IsHighSurrogate(text[length - 1])
                && char.IsLowSurrogate(text[length]))
            {
                length--;
            }
            return text.Substring(0, Math.Max(0, length));
        }
    }
}
