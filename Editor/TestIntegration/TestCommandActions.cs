using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

namespace Zh1Zh1.CSharpConsole.Editor.TestIntegration
{
    internal static class TestCommandActions
    {
        [CommandAction(
            "tests",
            "run",
            editorOnly: true,
            requiresProtectedInvocation: true,
            allowInBatch: false,
            summary: "Start one tracked Unity Test Framework run")]
        private static CommandResponse Run(
            CommandInvocation invocation,
            string mode,
            string[] testNames = null) =>
            UnityTestRunWorkflow.Run(invocation, mode, testNames);

        [CommandAction(
            "tests",
            "status",
            editorOnly: true,
            runOnMainThread: false,
            summary: "Read or briefly wait for one tracked Unity test run")]
        private static CommandResponse Status(
            string runId,
            int waitSeconds = 0) =>
            UnityTestRunWorkflow.Status(runId, waitSeconds);
    }
}
