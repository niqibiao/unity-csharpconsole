// LiteCompletionSpike.cs
//
// Verifies that LiteREPLCompiler's IREPLCompletionProvider implementation
// returns the right symbols for:
//   1. Member-access on a built-in type (`System.Math.` -> Sqrt, Abs, ...)
//   2. Member-access on a Lite-session slot (`x.` after `var x = 10`)
//   3. Bare-identifier lookup that should include the session slot itself
//      (just typing `x` should propose `x` as a Local)
// Each case runs against a fresh LiteREPLCompiler so state setup is
// deterministic. Results go to the Unity Console; last log line is the
// X/Y summary.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Zh1Zh1.CSharpConsole.Interface;
using Zh1Zh1.CSharpConsole.Lite;

namespace Zh1Zh1.CSharpConsole.EditorSpike
{
    public static class LiteCompletionSpike
    {
        [MenuItem("Console/Spike/Lite Completion")]
        public static void Run()
        {
            int pass = 0, fail = 0;

            // Case 1: member-access on a built-in type — no slot setup needed.
            // Expect at least Sqrt, Abs, Min, Max to surface (anywhere in result).
            {
                var c = new LiteREPLCompiler();
                var code = "System.Math.";
                var items = c.GetCompletions(code, code.Length, defines: "", defaultUsing: "");
                bool ok = ContainsAll(items, "Sqrt", "Abs", "Min", "Max");
                Report("C1 System.Math.<dot>", ok, items, expectAtLeast: new[] { "Sqrt", "Abs", "Min", "Max" });
                if (ok) pass++; else fail++;
            }

            // Case 2: member-access on a Lite-session slot — set up `var x = 10`
            // via a prior PrepareSubmission+Commit so the Roslyn chain knows `x`
            // is Int32 in the next submission's semantic model.
            {
                var c = new LiteREPLCompiler();
                c.PrepareSubmission("var x = 10;").Commit();
                var code = "x.";
                var items = c.GetCompletions(code, code.Length, defines: "", defaultUsing: "");
                // Int32 members: CompareTo, ToString, GetType (inherited).
                bool ok = ContainsAll(items, "CompareTo", "ToString", "GetType");
                Report("C2 slot-var x.<dot> (x: Int32)", ok, items, expectAtLeast: new[] { "CompareTo", "ToString", "GetType" });
                if (ok) pass++; else fail++;
            }

            // Case 3: bare-identifier lookup after declaring `x`. The completion
            // candidate list at top-level should include `x` as a symbol.
            {
                var c = new LiteREPLCompiler();
                c.PrepareSubmission("var x = 10;").Commit();
                var code = "x";
                var items = c.GetCompletions(code, code.Length, defines: "", defaultUsing: "");
                bool ok = items.Any(i => i.Label == "x");
                Report("C3 bare 'x' includes session slot", ok, items, expectAtLeast: new[] { "x" });
                if (ok) pass++; else fail++;
            }

            // Case 4: chained Lite session, multiple slots. Both `cnt` and `arr`
            // should show up in top-level completion.
            {
                var c = new LiteREPLCompiler();
                c.PrepareSubmission("var cnt = 0;").Commit();
                c.PrepareSubmission("var arr = new int[]{1,2,3};").Commit();
                var code = "";
                var items = c.GetCompletions(code, code.Length, defines: "", defaultUsing: "");
                bool ok = items.Any(i => i.Label == "cnt") && items.Any(i => i.Label == "arr");
                Report("C4 multi-slot session (cnt + arr)", ok, items, expectAtLeast: new[] { "cnt", "arr" });
                if (ok) pass++; else fail++;
            }

            // Case 5: UnityEngine type via default-using (no `using UnityEngine;`
            // needed since Lite's default-usings already include it). `Vector2.`
            // should propose static members.
            {
                var c = new LiteREPLCompiler();
                var code = "Vector2.";
                var items = c.GetCompletions(code, code.Length, defines: "", defaultUsing: "");
                bool ok = ContainsAll(items, "zero", "one", "up", "right");
                Report("C5 Vector2.<dot> (default-using UnityEngine)", ok, items, expectAtLeast: new[] { "zero", "one", "up", "right" });
                if (ok) pass++; else fail++;
            }

            var summary = $"Spike Lite Completion: {pass}/{pass + fail} PASS ({fail} fail)";
            if (fail == 0) Debug.Log(summary);
            else Debug.LogError(summary);
        }

        private static bool ContainsAll(List<CompletionItem> items, params string[] expected)
        {
            var set = new HashSet<string>(items.Select(i => i.Label), StringComparer.Ordinal);
            return expected.All(set.Contains);
        }

        private static void Report(string label, bool ok, List<CompletionItem> items, string[] expectAtLeast)
        {
            if (ok)
            {
                Debug.Log($"[Spike Completion PASS] {label}  ({items.Count} items, expected-subset all present)");
            }
            else
            {
                var missing = expectAtLeast.Where(e => items.All(i => i.Label != e)).ToArray();
                var sample = string.Join(", ", items.Take(20).Select(i => i.Label));
                Debug.LogError($"[Spike Completion FAIL] {label}  missing=[{string.Join(", ", missing)}]  first20=[{sample}]  totalItems={items.Count}");
            }
        }
    }
}
