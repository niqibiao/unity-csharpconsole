// P1-6 performance baseline: BCL interpreter vs Roslyn-JIT for the same
// hot loop expression. Provides the "Lite on Editor Mono" path (B) so the
// caller can compare against editor-mode Roslyn (A) and Player IL2CPP
// runtime-mode Lite (C) via separate cs exec calls.
//
// Why a static helper instead of just running the bench through the normal
// exec path: the editor's normal exec path is Roslyn/Mono (A). To get the
// Lite/Mono path (B), we need to manually drive LiteREPLCompiler — which
// is exactly what this helper does.

using System;
using UnityEditor;
using UnityEngine;
using Zh1Zh1.CSharpConsole.Lite;

namespace Zh1Zh1.CSharpConsole.EditorSpike
{
    public static class LiteBenchmark
    {
        // Same code that the caller feeds into A and C; for B we wrap it
        // through LiteREPLCompiler. The returned object is whatever the bench
        // body evaluates to (typically a double — elapsed ms — that the bench
        // body computes internally with Stopwatch).
        public static object RunLite(string code)
        {
            var compiler = new LiteREPLCompiler();
            var del = compiler.Compile(code);
            return del.DynamicInvoke();
        }

        // Convenience: 1 warm-up + N timed runs, returns formatted line per run.
        // Caller's bench MUST return a double of elapsed ms (computed in-band
        // via Stopwatch) so the time we report is the runtime's view, not
        // the HTTP roundtrip view.
        public static string RunLiteRepeated(string code, int runs)
        {
            // Warm-up
            _ = RunLite(code);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < runs; i++)
            {
                var ms = (double)RunLite(code);
                sb.AppendLine($"  run {i + 1}: {ms:F2} ms");
            }
            return sb.ToString();
        }

        [MenuItem("Console/Spike/Lite Benchmark (Editor Mono)")]
        public static void RunMenu()
        {
            Debug.Log("[LiteBenchmark] Sqrt 1M loop (Lite / Editor Mono):\n" +
                      RunLiteRepeated(SqrtLoopBench, 3));
            Debug.Log("[LiteBenchmark] Int sum 10M loop (Lite / Editor Mono):\n" +
                      RunLiteRepeated(IntLoopBench, 3));
        }

        // Bench bodies are kept here so A and C use exactly the same code via
        // string literal copy. The body must end with the elapsed-ms double.
        public const string SqrtLoopBench =
            "var sw = System.Diagnostics.Stopwatch.StartNew();" +
            " double sum = 0;" +
            " for (int i = 0; i < 1000000; i++) sum = sum + System.Math.Sqrt(i);" +
            " sw.Stop();" +
            " sw.Elapsed.TotalMilliseconds";

        public const string IntLoopBench =
            "var sw = System.Diagnostics.Stopwatch.StartNew();" +
            " int s = 0;" +
            " for (int i = 0; i < 10000000; i++) s = s + i;" +
            " sw.Stop();" +
            " sw.Elapsed.TotalMilliseconds";
    }
}
