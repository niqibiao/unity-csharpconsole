// LiteTranslatorP1Spike.cs
//
// Verifies the §6 P1-1 translator edge cases shipped on feat/lite-mode:
//   A. `string + X` concat  (lowers to string.Concat, not Expression.Add)
//   B. enum bitwise | & ^   (lifts to underlying integral)
//   C. `new[]{...}` implicit-typed array creation
//
// Each case instantiates a fresh LiteREPLCompiler and runs the submission
// through the same path /compile uses on the editor side — translator ->
// Expression tree -> lambda.Compile(preferInterpretation: true) -> invoke.
// preferInterpretation:true matches the player's BCL interpreter, so a PASS
// here is evidence the player will agree.
//
// Run from menu: Console/Spike/Translator P1. Results go to the Unity Console;
// last log line is the X/Y summary.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Zh1Zh1.CSharpConsole.Lite;

namespace Zh1Zh1.CSharpConsole.EditorSpike
{
    public static class LiteTranslatorP1Spike
    {
        private readonly struct Case
        {
            public readonly string Label;
            public readonly string Code;
            public readonly object Expected;
            public Case(string label, string code, object expected)
            {
                Label = label; Code = code; Expected = expected;
            }
        }

        [MenuItem("Console/Spike/Translator P1 (string + enum + new[])")]
        public static void Run()
        {
            var cases = new List<Case>
            {
                // A. string + X  — exercises both string.Concat overloads
                new Case("A1 string+string",   "\"a\" + \"b\"",                 "ab"),
                new Case("A2 string+int",      "\"x=\" + 5",                    "x=5"),
                new Case("A3 int+string",      "5 + \"=x\"",                    "5=x"),
                new Case("A4 string+null",     "\"a\" + (string)null",          "a"),
                new Case("A5 chain s+s+s",     "\"a\" + \"b\" + \"c\"",         "abc"),

                // B. enum bitwise  — exercises Or / And / ExclusiveOr lift
                new Case("B1 enum |",
                    "System.IO.FileAccess.Read | System.IO.FileAccess.Write",
                    System.IO.FileAccess.ReadWrite),
                new Case("B2 enum &",
                    "(System.AttributeTargets.Class | System.AttributeTargets.Struct) & System.AttributeTargets.Class",
                    System.AttributeTargets.Class),
                new Case("B3 enum ^",
                    "System.IO.FileShare.Read ^ System.IO.FileShare.Write",
                    System.IO.FileShare.Read ^ System.IO.FileShare.Write),
                new Case("B4 non-enum | int",  "1 | 2",                         3),
                new Case("B5 non-enum & int",  "6 & 3",                         2),

                // C. new[]{...}  — implicit element-type inference
                new Case("C1 int[].Length",    "new[]{1,2,3}.Length",           3),
                new Case("C2 int[][1]",        "new[]{1,2,3}[1]",               2),
                new Case("C3 string[][2]",     "new[]{\"a\",\"b\",\"c\"}[2]",   "c"),
                new Case("C4 double mix [0]",  "new[]{1.0, 2, 3}[0]",           1.0),
                new Case("C5 single element",  "new[]{42}[0]",                  42),
            };

            int pass = 0, fail = 0;
            var failed = new List<string>();

            foreach (var c in cases)
            {
                try
                {
                    var compiler = new LiteREPLCompiler();
                    var del = compiler.Compile(c.Code);
                    var actual = del.DynamicInvoke();
                    if (Equals(actual, c.Expected))
                    {
                        pass++;
                        Debug.Log($"[Spike P1 PASS] {c.Label}  =>  {Repr(actual)}");
                    }
                    else
                    {
                        fail++;
                        var msg = $"[Spike P1 FAIL] {c.Label}  expected={Repr(c.Expected)} actual={Repr(actual)}";
                        Debug.LogError(msg);
                        failed.Add(msg);
                    }
                }
                catch (Exception ex)
                {
                    fail++;
                    // Unwrap TargetInvocationException so the real cause is visible
                    // in the Console — DynamicInvoke wraps everything.
                    var inner = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null
                        ? tie.InnerException
                        : ex;
                    var msg = $"[Spike P1 FAIL] {c.Label}  threw: {inner.GetType().Name}: {inner.Message}";
                    Debug.LogError(msg);
                    failed.Add(msg);
                }
            }

            var summary = $"Spike Translator P1: {pass}/{pass + fail} PASS ({fail} fail)";
            if (fail == 0) Debug.Log(summary);
            else Debug.LogError(summary);
        }

        private static string Repr(object v)
        {
            if (v == null) return "null";
            if (v is string s) return "\"" + s + "\"";
            return $"{v} ({v.GetType().Name})";
        }
    }
}
