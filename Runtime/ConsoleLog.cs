using System;
using UnityEngine;

namespace Zh1Zh1.CSharpConsole
{
    public enum ConsoleLogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
        None = 4,
    }

    public static class ConsoleLog
    {
        private const string Prefix = "[C#Console]";

        public static ConsoleLogLevel Level { get; set; } = ConsoleLogLevel.Info;

        public static void Debug(string message)
        {
            if (!IsEnabled(ConsoleLogLevel.Debug))
            {
                return;
            }

            UnityEngine.Debug.Log(Format(message));
        }

        public static void Info(string message)
        {
            if (!IsEnabled(ConsoleLogLevel.Info))
            {
                return;
            }

            UnityEngine.Debug.Log(Format(message));
        }

        public static void Warning(string message)
        {
            if (!IsEnabled(ConsoleLogLevel.Warning))
            {
                return;
            }

            UnityEngine.Debug.LogWarning(Format(message));
        }

        public static void Error(string message)
        {
            if (!IsEnabled(ConsoleLogLevel.Error))
            {
                return;
            }

            UnityEngine.Debug.LogError(Format(message));
        }

        public static void Exception(Exception exception, string context = null)
        {
            if (exception == null)
            {
                Error(context ?? "Unknown exception");
                return;
            }

            if (!IsEnabled(ConsoleLogLevel.Error))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(context))
            {
                UnityEngine.Debug.LogError(Format(exception.ToString()));
                return;
            }

            UnityEngine.Debug.LogError(Format($"{context}: {exception}"));
        }

        public static bool IsEnabled(ConsoleLogLevel level)
        {
            return level >= Level && Level != ConsoleLogLevel.None;
        }

        public static string Format(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return Prefix;
            }

            return $"{Prefix} {message}";
        }
    }
}
