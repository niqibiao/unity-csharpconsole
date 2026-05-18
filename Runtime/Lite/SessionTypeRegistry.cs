using System;
using System.Collections.Generic;

namespace Zh1Zh1.CSharpConsole.Lite
{
    // Session-scoped Type ↔ int registry for Lite-mode binary wire protocol
    // (Docs~/ExpressionInterpreterFeasibility_zh.md §3.1 typeID 注册表).
    //
    // Wire model:
    //   - Writer side allocates ids via GetOrRegister(Type); each new allocation
    //     appends a TypeRegEntry to the delta buffer. After serialization, the
    //     envelope assembler calls FlushDelta() to drain the buffer into the
    //     `typeReg` JSON array.
    //   - Reader side receives the envelope's typeReg array and calls Register(id, type)
    //     for each entry. Reader does NOT add to its own delta buffer (the buffer is
    //     writer-only).
    //   - Both sides carry an `epoch` int. The writer bumps epoch on REPL :reset and
    //     on player-restart-detected resync. Reader compares the envelope's epoch
    //     to its local epoch — a mismatch triggers needsResync handshake.
    //
    // Single-flight invariant (§3.1 clause 9): no internal locking. Serialization
    // is serialized at the main-thread layer (MainThreadRequestRunner semaphore).
    // If a future caller introduces concurrent submission, lock at the call site
    // around "GetOrRegister + WriteRoot + FlushDelta" as one atomic.
    public sealed class SessionTypeRegistry
    {
        private readonly Dictionary<Type, int> m_TypeToId = new();
        private readonly Dictionary<int, Type> m_IdToType = new();
        private readonly List<TypeRegEntry> m_DeltaBuffer = new();
        private int m_NextId = 1;
        private int m_Epoch;

        public int Count => m_IdToType.Count;
        public int Epoch => m_Epoch;
        public int PendingDeltaCount => m_DeltaBuffer.Count;

        // Writer-side: allocate or look up a typeId for the given Type.
        // New allocations are appended to the delta buffer.
        public int GetOrRegister(Type t)
        {
            if (t == null)
                throw new LiteWireException("E_TYPEREG_NULL_TYPE", "cannot register null Type");

            if (m_TypeToId.TryGetValue(t, out var id))
                return id;

            id = m_NextId++;
            m_TypeToId[t] = id;
            m_IdToType[id] = t;
            m_DeltaBuffer.Add(new TypeRegEntry(id, t.AssemblyQualifiedName));
            return id;
        }

        // Reader-side: ingest a (id, type) pair from a peer's delta. Idempotent on
        // same (id, type); throws E_TYPEREG_CONFLICT on same id with different type.
        // Does not add to the local delta buffer.
        public void Register(int id, Type t)
        {
            if (id == 0)
                throw new LiteWireException(
                    "E_TYPEREG_CONFLICT",
                    "typeId 0 is reserved as the unregistered sentinel (§3.1 clause 1); cannot be registered");
            if (t == null)
                throw new LiteWireException("E_TYPEREG_NULL_TYPE", "cannot register null Type");

            if (m_IdToType.TryGetValue(id, out var existing))
            {
                if (existing != t)
                    throw new LiteWireException(
                        "E_TYPEREG_CONFLICT",
                        $"typeId {id} already bound to {existing.AssemblyQualifiedName}, refusing rebind to {t.AssemblyQualifiedName}");
                return;
            }
            m_IdToType[id] = t;
            m_TypeToId[t] = id;
            if (id >= m_NextId) m_NextId = id + 1;
        }

        // Reader-side: look up a Type by id. Throws E_TYPEREG_UNKNOWN_ID if the id
        // has not been Register-ed (peer must have omitted it from the envelope delta,
        // indicating an out-of-sync envelope — caller should set needsResync=true on
        // the response envelope to trigger a full-registry resync).
        public Type Resolve(int id)
        {
            if (!m_IdToType.TryGetValue(id, out var t))
                throw new LiteWireException(
                    "E_TYPEREG_UNKNOWN_ID",
                    $"typeId {id} not present in registry (registry size={m_IdToType.Count})");
            return t;
        }

        // Writer-side: drain the delta buffer. Called by the envelope assembler
        // after WriteRoot completes. Returns the new (id, AQN) entries allocated
        // during the most recent serialization and clears the buffer.
        public IReadOnlyList<TypeRegEntry> FlushDelta()
        {
            if (m_DeltaBuffer.Count == 0)
                return Array.Empty<TypeRegEntry>();
            var result = m_DeltaBuffer.ToArray();
            m_DeltaBuffer.Clear();
            return result;
        }

        // Snapshot of all (id, type) entries currently held. Used by writer-side
        // resync construction (PrepareResync) and by tests inspecting state.
        public IEnumerable<KeyValuePair<int, Type>> Snapshot() => m_IdToType;

        // Writer-side: bump epoch and clear local state. Called on REPL :reset
        // (host editor) and on detected player restart. After BumpEpoch the next
        // serialization will re-issue ids from 1; the envelope carries the new
        // epoch so the peer knows to rebuild.
        public void BumpEpoch()
        {
            m_Epoch++;
            m_TypeToId.Clear();
            m_IdToType.Clear();
            m_DeltaBuffer.Clear();
            m_NextId = 1;
        }

        // Writer-side: build a full-registry snapshot envelope payload for a
        // resync frame. Increments the epoch (peer compares its local epoch to
        // detect the resync) and returns the full mapping so the envelope
        // assembler can ship it as `typeReg` with `needsResync` set.
        // The local table is preserved (writer continues using the existing
        // allocations after resync — they survive into the new epoch).
        public IReadOnlyList<TypeRegEntry> PrepareResync()
        {
            m_Epoch++;
            m_DeltaBuffer.Clear();
            var entries = new TypeRegEntry[m_IdToType.Count];
            int i = 0;
            foreach (var kv in m_IdToType)
                entries[i++] = new TypeRegEntry(kv.Key, kv.Value.AssemblyQualifiedName);
            return entries;
        }

        // Reader-side: ingest a full-registry snapshot from a resync frame.
        // Wipes local state, adopts the peer's epoch + full registry. Subsequent
        // Resolve calls use the rebuilt table.
        public void IngestResync(int newEpoch, IReadOnlyList<TypeRegEntry> entries)
        {
            if (entries == null)
                throw new LiteWireException("E_TYPEREG_NULL_RESYNC", "cannot ingest null resync entries");

            m_TypeToId.Clear();
            m_IdToType.Clear();
            m_DeltaBuffer.Clear();
            m_NextId = 1;
            m_Epoch = newEpoch;

            foreach (var entry in entries)
            {
                if (entry.Id == 0)
                    throw new LiteWireException(
                        "E_TYPEREG_CONFLICT",
                        "resync entry has typeId=0 which is the reserved unregistered sentinel (§3.1 clause 1)");
                if (entry.Aqn == null)
                    throw new LiteWireException(
                        "E_TYPEREG_RESYNC_UNRESOLVABLE",
                        $"resync entry id={entry.Id} has null AQN");
                var t = Type.GetType(entry.Aqn, throwOnError: false);
                if (t == null)
                    throw new LiteWireException(
                        "E_TYPEREG_RESYNC_UNRESOLVABLE",
                        $"resync entry id={entry.Id} aqn='{entry.Aqn}' cannot be resolved in this process; peer may have referenced an assembly not loaded here");
                if (m_IdToType.TryGetValue(entry.Id, out var existing))
                {
                    if (existing != t)
                        throw new LiteWireException(
                            "E_TYPEREG_CONFLICT",
                            $"resync entry id={entry.Id} aqn='{entry.Aqn}' conflicts with earlier entry binding the same id to '{existing.AssemblyQualifiedName}'");
                    continue;
                }
                m_IdToType[entry.Id] = t;
                m_TypeToId[t] = entry.Id;
                if (entry.Id >= m_NextId) m_NextId = entry.Id + 1;
            }
        }

        // Reader-side: validate the incoming envelope epoch against local state.
        // Returns true if local state needs resync (caller should set
        // needsResync=true on the response envelope to request a resync frame).
        public bool DetectEpochMismatch(int envelopeEpoch)
        {
            return envelopeEpoch != m_Epoch;
        }
    }

    // One (id, AQN) pair carried in envelope's typeReg array. AQN format is
    // System.Type.AssemblyQualifiedName, which is portable across processes
    // when the same assemblies are loaded on both sides.
    public readonly struct TypeRegEntry
    {
        public readonly int Id;
        public readonly string Aqn;

        public TypeRegEntry(int id, string aqn)
        {
            Id = id;
            Aqn = aqn;
        }
    }
}
