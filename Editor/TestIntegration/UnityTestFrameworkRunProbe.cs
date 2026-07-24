using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor.TestTools.TestRunner.Api;

namespace Zh1Zh1.CSharpConsole.Editor.TestIntegration
{
    /// <summary>
    /// Isolates the version-pinned Test Framework implementation detail needed
    /// to correlate global callbacks with the GUID returned by Execute().
    /// The workflow fails closed whenever this seam cannot prove one owner.
    /// </summary>
    internal static class UnityTestFrameworkRunProbe
    {
        internal sealed class Snapshot
        {
            internal bool available;
            internal string error = "";
            internal string[] activeRunIds = Array.Empty<string>();
        }

        private const string HolderTypeName =
            "UnityEditor.TestTools.TestRunner.TestRun.TestJobDataHolder";

        private static bool s_Resolved;
        private static string s_ResolutionError = "";
        private static PropertyInfo s_InstanceProperty;
        private static FieldInfo s_TestRunsField;
        private static FieldInfo s_GuidField;
        private static FieldInfo s_IsRunningField;

        internal static Snapshot Capture()
        {
            try
            {
                Resolve();
                if (!string.IsNullOrEmpty(s_ResolutionError))
                {
                    return Unavailable(s_ResolutionError);
                }

                var holder = s_InstanceProperty.GetValue(null);
                if (holder == null)
                {
                    return Unavailable("Test Framework run holder is unavailable");
                }

                if (!(s_TestRunsField.GetValue(holder) is IEnumerable runs))
                {
                    return Unavailable("Test Framework run collection is unavailable");
                }

                var activeIds = new List<string>();
                foreach (var run in runs)
                {
                    if (run == null)
                    {
                        continue;
                    }

                    var isRunningValue = s_IsRunningField.GetValue(run);
                    if (!(isRunningValue is bool isRunning) || !isRunning)
                    {
                        continue;
                    }

                    var rawGuid = s_GuidField.GetValue(run) as string;
                    if (!Guid.TryParse(rawGuid, out var parsedGuid))
                    {
                        return Unavailable(
                            "Test Framework exposed an active run with an invalid id");
                    }

                    activeIds.Add(parsedGuid.ToString("D"));
                }

                activeIds.Sort(StringComparer.OrdinalIgnoreCase);
                return new Snapshot
                {
                    available = true,
                    activeRunIds = activeIds.ToArray()
                };
            }
            catch (Exception e)
            {
                return Unavailable(
                    $"Test Framework run probe failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void Resolve()
        {
            if (s_Resolved)
            {
                return;
            }

            s_Resolved = true;
            var assembly = typeof(TestRunnerApi).Assembly;
            var holderType = assembly.GetType(HolderTypeName, throwOnError: false);
            if (holderType == null)
            {
                s_ResolutionError = "Test Framework run holder type was not found";
                return;
            }

            s_InstanceProperty = holderType.GetProperty(
                "instance",
                BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Static
                | BindingFlags.FlattenHierarchy);
            s_TestRunsField = holderType.GetField(
                "TestRuns",
                BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Instance);

            var runType = s_TestRunsField?.FieldType.IsGenericType == true
                ? s_TestRunsField.FieldType.GetGenericArguments()[0]
                : null;
            s_GuidField = runType?.GetField(
                "guid",
                BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Instance);
            s_IsRunningField = runType?.GetField(
                "isRunning",
                BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Instance);

            if (s_InstanceProperty == null
                || s_TestRunsField == null
                || s_GuidField == null
                || s_IsRunningField == null)
            {
                s_ResolutionError =
                    "Test Framework run holder shape does not match the supported Unity 2022 integration";
            }
        }

        private static Snapshot Unavailable(string error)
        {
            return new Snapshot
            {
                available = false,
                error = error ?? "Test Framework run probe is unavailable"
            };
        }
    }
}
