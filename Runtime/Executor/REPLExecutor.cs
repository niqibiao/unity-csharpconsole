using System;
using System.Reflection;
using System.Threading.Tasks;
using Zh1Zh1.CSharpConsole.Interface;
using UnityEngine;

namespace Zh1Zh1.CSharpConsole.Executor
{
    /// <summary>
    /// Provides shared DLL loading and execution logic.
    /// </summary>
    public class REPLExecutor : IREPLExecutor
    {
        private readonly object[] m_SubmissionArray = new object[IREPLExecutor.MAX_SUBMISSION_ID + 1];

        public async Task<object> ExecuteAsync(byte[] assemblyBytes, string scriptClassName)
        {
            if (assemblyBytes == null || assemblyBytes.Length == 0)
            {
                return "DLL bytes is null or empty";
            }

            if (string.IsNullOrEmpty(scriptClassName))
            {
                return "Script class name is null or empty";
            }

            try
            {
                var assembly = Assembly.Load(assemblyBytes);

                var scriptType = assembly.GetType(scriptClassName);
                if (scriptType == null)
                {
                    return $"Cannot find script type: {scriptClassName}";
                }

                var factory = scriptType.GetMethod("<Factory>", BindingFlags.Public | BindingFlags.Static);
                if (factory == null)
                {
                    return $"Cannot find <Factory> method in {scriptClassName}";
                }

                var task = (Task<object>)factory.Invoke(null, new object[] { m_SubmissionArray });
                return await task;
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException;
                ConsoleLog.Warning($"Execution error: {inner?.Message}\n{inner?.StackTrace}");
                return ConsoleLog.Format($"Execution error: {inner?.Message}");
            }
            catch (Exception ex)
            {
                ConsoleLog.Warning($"Load error: {ex.Message}\n{ex.StackTrace}");
                return ConsoleLog.Format($"Load error: {ex.Message}");
            }
        }
    }
}
