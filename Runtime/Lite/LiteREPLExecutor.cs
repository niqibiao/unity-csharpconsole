using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;
using UnityEngine;

namespace Zh1Zh1.CSharpConsole.Lite
{
    // Player-side Lite-mode REPL executor.
    //
    // Flow per /execute request (when bodyBinary is non-empty):
    //   1. Compare envelopeEpoch against local m_TypeReg.Epoch.
    //      Mismatch → return NeedsResync=true without executing.
    //   2. Ingest typeRegDelta entries via m_TypeReg.Register(id, type).
    //   3. Decode bodyBinary via LiteWireReader → LambdaExpression.
    //   4. lambda.Compile(preferInterpretation: true) — uses
    //      System.Linq.Expressions.Interpreter, IL2CPP-safe (link.xml entry
    //      ships in task 7).
    //   5. Invoke and return result.
    //
    // Session state: m_Slots dictionary (shared with the LiteWireReader for
    // SlotsRef anchoring) and m_TypeReg. Reset() clears both and bumps epoch.
    public sealed class LiteREPLExecutor : ILiteREPLExecutor
    {
        private readonly Dictionary<string, object> m_Slots = new(StringComparer.Ordinal);
        private readonly SessionTypeRegistry m_TypeReg = new();

        public async Task<LiteExecuteOutcome> ExecuteAsync(byte[] bodyBinary,
            TypeRegEntryDto[] typeRegDelta,
            int envelopeEpoch)
        {
            if (bodyBinary == null || bodyBinary.Length == 0)
            {
                return new LiteExecuteOutcome
                {
                    ErrorCode = "E_LITE_EMPTY_BODY",
                    Result = "binary body is null or empty",
                    ServerEpoch = m_TypeReg.Epoch
                };
            }

            if (m_TypeReg.DetectEpochMismatch(envelopeEpoch))
            {
                return new LiteExecuteOutcome
                {
                    ErrorCode = "E_TYPEREG_EPOCH_MISMATCH",
                    Result = $"epoch mismatch: envelope={envelopeEpoch}, local={m_TypeReg.Epoch}",
                    NeedsResync = true,
                    ServerEpoch = m_TypeReg.Epoch
                };
            }

            try
            {
                if (typeRegDelta != null)
                {
                    foreach (var entry in typeRegDelta)
                    {
                        if (string.IsNullOrEmpty(entry.aqn))
                        {
                            return new LiteExecuteOutcome
                            {
                                ErrorCode = "E_TYPEREG_RESYNC_UNRESOLVABLE",
                                Result = $"typeReg entry id={entry.id} has empty/null AQN",
                                NeedsResync = true,
                                ServerEpoch = m_TypeReg.Epoch
                            };
                        }
                        var t = Type.GetType(entry.aqn, throwOnError: false);
                        if (t == null)
                        {
                            return new LiteExecuteOutcome
                            {
                                ErrorCode = "E_TYPEREG_RESYNC_UNRESOLVABLE",
                                Result = $"typeReg entry id={entry.id} aqn='{entry.aqn}' cannot be resolved in this process",
                                NeedsResync = true,
                                ServerEpoch = m_TypeReg.Epoch
                            };
                        }
                        m_TypeReg.Register(entry.id, t);
                    }
                }

                var reader = new LiteWireReader(m_TypeReg, m_Slots);
                var root = reader.ReadRoot(bodyBinary);
                if (!(root is LambdaExpression lambda))
                {
                    return new LiteExecuteOutcome
                    {
                        ErrorCode = "E_LITE_NOT_LAMBDA",
                        Result = $"root expression is {root.NodeType}, expected Lambda",
                        ServerEpoch = m_TypeReg.Epoch
                    };
                }

                var compiled = lambda.Compile(preferInterpretation: true);
                // Execute synchronously on the calling thread. The HTTP path
                // already dispatches us onto the main thread via
                // MainThreadRequestRunner; the BCL Interpreter runs there.
                // Don't wrap with Task.Run: a sync caller doing
                // GetAwaiter().GetResult() would deadlock against the await's
                // main-thread SynchronizationContext capture.
                //
                // Prefer the direct delegate invocation over DynamicInvoke to
                // skip reflection-based binding and avoid TargetInvocationException
                // wrapping. Editor emits root as Expression<Func<object>> so the
                // cast hits the common path; the TargetInvocationException catch
                // below only fires on the DynamicInvoke fallback.
                var resultObj = compiled is Func<object> f
                    ? f()
                    : compiled.DynamicInvoke();
                await Task.CompletedTask;
                return new LiteExecuteOutcome
                {
                    Result = resultObj?.ToString() ?? "",
                    ServerEpoch = m_TypeReg.Epoch
                };
            }
            catch (LiteWireException ex)
            {
                ConsoleLog.Warning($"Lite execute LiteWireException [{ex.ErrorCode}]: {ex.Message}\n{ex.StackTrace}");
                return new LiteExecuteOutcome
                {
                    ErrorCode = ex.ErrorCode,
                    Result = ex.Message,
                    NeedsResync = ex.ErrorCode == "E_TYPEREG_UNKNOWN_ID" || ex.ErrorCode == "E_TYPEREG_EPOCH_MISMATCH",
                    ServerEpoch = m_TypeReg.Epoch
                };
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                var inner = ex.InnerException;
                ConsoleLog.Warning($"Lite execute target invocation: {inner?.Message}\n{inner?.StackTrace}");
                return new LiteExecuteOutcome
                {
                    ErrorCode = "E_LITE_EXEC_ERROR",
                    Result = ConsoleLog.Format($"Execution error: {inner?.Message}"),
                    ServerEpoch = m_TypeReg.Epoch
                };
            }
            catch (Exception ex)
            {
                ConsoleLog.Warning($"Lite execute exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                return new LiteExecuteOutcome
                {
                    ErrorCode = "E_LITE_EXEC_ERROR",
                    Result = ConsoleLog.Format($"Lite execute error: {ex.Message}"),
                    ServerEpoch = m_TypeReg.Epoch
                };
            }
        }

        public void Reset()
        {
            m_Slots.Clear();
            m_TypeReg.BumpEpoch();
        }
    }
}
