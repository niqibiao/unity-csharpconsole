using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
#if ZH1ZH1_CSC_TEST_FRAMEWORK
using UnityEditor.TestTools.TestRunner.Api;
#endif
#endif
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    internal static class TestRunnerCommandActions
    {
        internal static void Register(CommandRouter router)
        {
#if UNITY_EDITOR
            router.RegisterAttributedHandlers(typeof(TestRunnerCommandActions));
#endif
        }

#if UNITY_EDITOR
        private const string RUN_ACTIVE_SESSION_KEY = "Zh1Zh1.CSharpConsole.TestRunActive";
        private const int MAX_REPORTED_FAILURES = 25;
        private const int MAX_FAILURE_MESSAGE_LENGTH = 2000;

        [Serializable]
        private sealed class FailureRecord
        {
            public string testName = "";
            public string message = "";
        }

        [Serializable]
        private sealed class TestRunState
        {
            [CommandField(AllowedValues = new[] { "idle", "running", "finished", "aborted" })]
            public string phase = "idle";
            public string runId = "";
            public string mode = "";
            public string startedUtc = "";
            public string finishedUtc = "";
            public double durationSeconds;
            public int passed;
            public int failed;
            public int skipped;
            public int inconclusive;
            public FailureRecord[] failures = Array.Empty<FailureRecord>();
            public string message = "";
        }

        [Serializable]
        private sealed class RunStartResult
        {
            public string runId = "";
            public string mode = "";
            public bool started;
        }

        [CommandAction(
            "editor",
            "test.run",
            editorOnly: true,
            summary: "Start a Unity Test Framework run and return its run id",
            resultType: typeof(RunStartResult))]
        private static CommandResponse Run(
            [CommandArgument(AllowedValues = new[] { "editMode", "playMode" }, AllowedValuesIgnoreCase = true)]
            string mode = "editMode",
            string[] testNames = null,
            string[] groupNames = null,
            bool force = false)
        {
#if ZH1ZH1_CSC_TEST_FRAMEWORK
            var normalizedMode = string.Equals(mode, "playMode", StringComparison.OrdinalIgnoreCase)
                ? "playMode"
                : "editMode";

            var state = LoadState();
            ReconcileOrphanedRun(state);
            if (state.phase == "running" && !force)
            {
                return CommandResponseFactory.ValidationError(
                    $"A test run is already in progress (runId '{state.runId}', started {state.startedUtc}). " +
                    "Wait for editor/test.status to report 'finished', or pass force=true to supersede a stale record.");
            }

            var filter = new Filter
            {
                testMode = normalizedMode == "playMode" ? TestMode.PlayMode : TestMode.EditMode
            };
            if (testNames != null && testNames.Length > 0)
            {
                filter.testNames = testNames;
            }
            if (groupNames != null && groupNames.Length > 0)
            {
                filter.groupNames = groupNames;
            }

            EnsureCallbacksRegistered();

            string runId;
            try
            {
                var api = ScriptableObject.CreateInstance<TestRunnerApi>();
                runId = api.Execute(new ExecutionSettings(filter)) ?? "";
            }
            catch (Exception ex)
            {
                return CommandResponseFactory.ValidationError($"Failed to start the test run: {ex.Message}");
            }

            var newState = new TestRunState
            {
                phase = "running",
                runId = runId,
                mode = normalizedMode,
                startedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                message = "Test run started"
            };
            SaveState(newState);
            SessionState.SetBool(RUN_ACTIVE_SESSION_KEY, true);

            var result = new RunStartResult
            {
                runId = runId,
                mode = normalizedMode,
                started = true
            };
            return CommandResponseFactory.Ok($"Started {normalizedMode} test run", JsonUtility.ToJson(result));
#else
            return CommandResponseFactory.ValidationError(
                "com.unity.test-framework is not installed in this project, so editor/test.run cannot start a run. " +
                "Add com.unity.test-framework to Packages/manifest.json and let Unity resolve it first.");
#endif
        }

        [CommandAction(
            "editor",
            "test.status",
            editorOnly: true,
            summary: "Get the state and results of the latest Unity Test Framework run",
            resultType: typeof(TestRunState))]
        private static CommandResponse Status()
        {
#if ZH1ZH1_CSC_TEST_FRAMEWORK
            var state = LoadState();
            ReconcileOrphanedRun(state);
            var summary = state.phase switch
            {
                "running" => $"Test run '{state.runId}' is running",
                "finished" => $"Test run finished: {state.passed} passed, {state.failed} failed, {state.skipped} skipped",
                "aborted" => "The last test run was aborted before finishing",
                _ => "No test run has been recorded"
            };
            return CommandResponseFactory.Ok(summary, JsonUtility.ToJson(state));
#else
            return CommandResponseFactory.ValidationError(
                "com.unity.test-framework is not installed in this project, so editor/test.status has no runs to report. " +
                "Add com.unity.test-framework to Packages/manifest.json and let Unity resolve it first.");
#endif
        }

#if ZH1ZH1_CSC_TEST_FRAMEWORK
        private static RunCallbacks s_Callbacks;
        private static TestRunnerApi s_CallbackApi;

        [InitializeOnLoadMethod]
        private static void InitializeCallbackRegistration()
        {
            EnsureCallbacksRegistered();
            ReconcileOrphanedRun(LoadState());
        }

        private static void EnsureCallbacksRegistered()
        {
            if (s_Callbacks != null && s_CallbackApi != null)
            {
                return;
            }

            s_Callbacks = new RunCallbacks();
            s_CallbackApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            s_CallbackApi.hideFlags = HideFlags.HideAndDontSave;
            s_CallbackApi.RegisterCallbacks(s_Callbacks);
        }

        // A state file that says "running" while the session flag is unset means the
        // editor was restarted mid-run and the RunFinished callback will never come.
        private static void ReconcileOrphanedRun(TestRunState state)
        {
            if (state.phase != "running" || SessionState.GetBool(RUN_ACTIVE_SESSION_KEY, false))
            {
                return;
            }

            state.phase = "aborted";
            state.finishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            state.message = "The editor restarted before the test run finished";
            SaveState(state);
        }

        private sealed class RunCallbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
                var state = LoadState();
                if (state.phase == "running")
                {
                    return;
                }

                // A run launched outside editor/test.run (for example from the Test
                // Runner window) is still tracked so test.status stays truthful.
                SaveState(new TestRunState
                {
                    phase = "running",
                    startedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    message = "Test run started outside editor/test.run"
                });
                SessionState.SetBool(RUN_ACTIVE_SESSION_KEY, true);
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                var state = LoadState();
                state.phase = "finished";
                state.finishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                state.durationSeconds = result.Duration;
                state.passed = result.PassCount;
                state.failed = result.FailCount;
                state.skipped = result.SkipCount;
                state.inconclusive = result.InconclusiveCount;
                state.failures = CollectFailures(result);
                state.message = state.failed > 0
                    ? $"{state.failed} test(s) failed"
                    : "All tests passed";
                SaveState(state);
                SessionState.SetBool(RUN_ACTIVE_SESSION_KEY, false);
            }

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
            }
        }

        private static FailureRecord[] CollectFailures(ITestResultAdaptor root)
        {
            var failures = new List<FailureRecord>();
            var stack = new Stack<ITestResultAdaptor>();
            stack.Push(root);

            while (stack.Count > 0 && failures.Count < MAX_REPORTED_FAILURES)
            {
                var node = stack.Pop();
                if (node.HasChildren)
                {
                    foreach (var child in node.Children)
                    {
                        stack.Push(child);
                    }
                    continue;
                }

                if (node.TestStatus != TestStatus.Failed)
                {
                    continue;
                }

                var message = node.Message ?? "";
                if (message.Length > MAX_FAILURE_MESSAGE_LENGTH)
                {
                    message = message.Substring(0, MAX_FAILURE_MESSAGE_LENGTH);
                }

                failures.Add(new FailureRecord
                {
                    testName = node.Test?.FullName ?? node.FullName ?? "",
                    message = message
                });
            }

            return failures.ToArray();
        }

        private static string GetStateFilePath()
        {
            // Library, not Temp: the editor recreates Temp on every start, which
            // would erase the record needed to report an interrupted run as
            // aborted after a restart.
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "CSharpConsole", "test_run_state.json"));
        }

        private static TestRunState LoadState()
        {
            try
            {
                var path = GetStateFilePath();
                if (File.Exists(path))
                {
                    var state = JsonUtility.FromJson<TestRunState>(File.ReadAllText(path));
                    if (state != null)
                    {
                        return state;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CSharpConsole] Failed to read the test run state file: {ex.Message}");
            }

            return new TestRunState();
        }

        private static void SaveState(TestRunState state)
        {
            try
            {
                var path = GetStateFilePath();
                CommandHelpers.EnsureDirectoryExists(path);
                File.WriteAllText(path, JsonUtility.ToJson(state));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CSharpConsole] Failed to write the test run state file: {ex.Message}");
            }
        }
#endif
#endif
    }
}
