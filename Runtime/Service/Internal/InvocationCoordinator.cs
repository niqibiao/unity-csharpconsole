using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Zh1Zh1.CSharpConsole.Service.Internal
{
    internal enum InvocationClaimDisposition
    {
        Execute,
        Unprotected,
        Replay,
        InProgress,
        Conflict,
        OutcomeUnknown,
        Rejected
    }

    internal sealed class InvocationClaim
    {
        public InvocationClaimDisposition Disposition;
        public string InvocationId = "";
        public string TargetId = "";
        public string ServiceEpoch = "";
        public string Endpoint = "";
        public string RequestDigest = "";
        public string ResponseJson = "";
        public string Message = "";
        public string CreatedAtUtc = "";
        public string UpdatedAtUtc = "";
        public bool TerminalResponsePersisted;
    }

    /// <summary>
    /// Owns the durable at-most-once boundary for HTTP mutations.
    ///
    /// A started record is created before dispatch. Completed and outcome-unknown
    /// records are append-only terminal markers, so a process or domain reload
    /// cannot turn an ambiguous mutation back into an executable request.
    /// </summary>
    internal sealed class InvocationCoordinator
    {
        public const string InvocationIdHeader = "X-CSharpConsole-Invocation-Id";
        public const string TargetIdHeader = "X-CSharpConsole-Target-Id";
        public const int DedupeWindowSeconds = 86_400;

        private const int SchemaVersion = 1;
        private const string StartedState = "started";
        private const string CompletedState = "completed";
        private const string OutcomeUnknownState = "outcome_unknown";

        [Serializable]
        private sealed class IdentityRecord
        {
            public int schemaVersion = SchemaVersion;
            public string targetId = "";
            public int processId;
            public long processStartUtcTicks;
            public string serviceEpoch = "";
            public string projectRoot = "";
        }

        [Serializable]
        private sealed class LedgerRecord
        {
            public int schemaVersion = SchemaVersion;
            public string invocationId = "";
            public string targetId = "";
            public string serviceEpoch = "";
            public string endpoint = "";
            public string requestDigest = "";
            public string state = "";
            public string createdAtUtc = "";
            public string updatedAtUtc = "";
            public string responseJson = "";
            public string message = "";
        }

        private readonly object _gate = new object();
        private readonly HashSet<string> _active = new HashSet<string>(StringComparer.Ordinal);
        private readonly string _ledgerBaseRoot;
        private string _ledgerRoot;
        private string _targetId = "";
        private string _serviceEpoch = "";
        private bool _journalWritable;
        private DateTime _nextMaintenanceUtc = DateTime.MinValue;

        public InvocationCoordinator()
        {
            _ledgerBaseRoot = ResolveLedgerRoot();
            _ledgerRoot = _ledgerBaseRoot;
            InitializeJournal();
        }

        public string TargetId
        {
            get
            {
                lock (_gate)
                {
                    return _targetId;
                }
            }
        }

        public string ServiceEpoch
        {
            get
            {
                lock (_gate)
                {
                    return _serviceEpoch;
                }
            }
        }

        public bool JournalWritable
        {
            get
            {
                lock (_gate)
                {
                    return _journalWritable;
                }
            }
        }

        public void RestartServiceEpoch()
        {
            MarkOutstandingOutcomeUnknown(
                "The Unity service restarted before the invocation outcome was durably recorded.");
            InitializeJournal();
        }

        public InvocationClaim Claim(
            string invocationIdHeader,
            string targetIdHeader,
            string endpoint,
            byte[] exactBody)
        {
            var rawInvocationId = invocationIdHeader?.Trim() ?? "";
            var rawTargetId = targetIdHeader?.Trim() ?? "";

            if (string.IsNullOrEmpty(rawInvocationId) && string.IsNullOrEmpty(rawTargetId))
            {
                return new InvocationClaim
                {
                    Disposition = InvocationClaimDisposition.Unprotected,
                    TargetId = TargetId,
                    ServiceEpoch = ServiceEpoch,
                    Endpoint = endpoint ?? ""
                };
            }

            if (string.IsNullOrEmpty(rawInvocationId) || string.IsNullOrEmpty(rawTargetId))
            {
                return Reject(
                    rawInvocationId,
                    rawTargetId,
                    endpoint,
                    "Both invocation and target headers are required for at-most-once execution.");
            }

            if (!Guid.TryParse(rawInvocationId, out var parsedInvocationId))
            {
                return Reject(
                    rawInvocationId,
                    rawTargetId,
                    endpoint,
                    "Invocation id must be a UUID.");
            }

            var invocationId = parsedInvocationId.ToString("D");
            var requestDigest = ComputeDigest(exactBody ?? Array.Empty<byte>());

            lock (_gate)
            {
                if (!string.Equals(rawTargetId, _targetId, StringComparison.Ordinal))
                {
                    return new InvocationClaim
                    {
                        Disposition = InvocationClaimDisposition.Rejected,
                        InvocationId = invocationId,
                        TargetId = _targetId,
                        ServiceEpoch = _serviceEpoch,
                        Endpoint = endpoint ?? "",
                        RequestDigest = requestDigest,
                        Message = "Target id does not match this Unity service."
                    };
                }

                if (!_journalWritable)
                {
                    return new InvocationClaim
                    {
                        Disposition = InvocationClaimDisposition.Rejected,
                        InvocationId = invocationId,
                        TargetId = _targetId,
                        ServiceEpoch = _serviceEpoch,
                        Endpoint = endpoint ?? "",
                        RequestDigest = requestDigest,
                        Message = "Invocation journal is not writable; mutation was not dispatched."
                    };
                }

                if (HasCorruptRecord(invocationId))
                {
                    _journalWritable = false;
                    return RejectLocked(
                        invocationId,
                        endpoint,
                        requestDigest,
                        "Invocation journal contains a corrupt record; mutation was not dispatched.");
                }

                var existing = LoadExistingRecord(invocationId);
                if (existing != null && !_active.Contains(invocationId))
                {
                    if (IsExpired(existing))
                    {
                        if (DeleteInvocationFiles(invocationId))
                        {
                            existing = null;
                        }
                        else
                        {
                            _journalWritable = false;
                            return RejectLocked(
                                invocationId,
                                endpoint,
                                requestDigest,
                                "Expired invocation records could not be removed safely; mutation was not dispatched.");
                        }
                    }
                }

                if (existing != null)
                {
                    if (!Matches(existing, _targetId, endpoint, requestDigest))
                    {
                        return FromRecord(
                            existing,
                            InvocationClaimDisposition.Conflict,
                            "Invocation id was already used for a different target, endpoint, or exact request body.");
                    }

                    if (string.Equals(existing.state, CompletedState, StringComparison.Ordinal))
                    {
                        var replay = FromRecord(existing, InvocationClaimDisposition.Replay, "Replaying persisted response.");
                        replay.ResponseJson = existing.responseJson ?? "";
                        return replay;
                    }

                    if (string.Equals(existing.state, OutcomeUnknownState, StringComparison.Ordinal))
                    {
                        return FromRecord(
                            existing,
                            InvocationClaimDisposition.OutcomeUnknown,
                            string.IsNullOrEmpty(existing.message)
                                ? "The original invocation outcome is unknown and will not be dispatched again."
                                : existing.message);
                    }

                    if (_active.Contains(invocationId))
                    {
                        return FromRecord(
                            existing,
                            InvocationClaimDisposition.InProgress,
                            "The original invocation is still in progress.");
                    }

                    // A started record not owned by this live coordinator survived
                    // an interruption. Never guess that it did not mutate state.
                    var unknown = PersistOutcomeUnknown(existing, "The service restarted before the invocation outcome was durably recorded.");
                    return FromRecord(
                        unknown ?? existing,
                        InvocationClaimDisposition.OutcomeUnknown,
                        "The original invocation outcome is unknown and will not be dispatched again.");
                }

                var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                var started = new LedgerRecord
                {
                    invocationId = invocationId,
                    targetId = _targetId,
                    serviceEpoch = _serviceEpoch,
                    endpoint = endpoint ?? "",
                    requestDigest = requestDigest,
                    state = StartedState,
                    createdAtUtc = now,
                    updatedAtUtc = now
                };

                try
                {
                    WriteNewRecord(GetRecordPath(invocationId, StartedState), started);
                    _active.Add(invocationId);
                }
                catch (IOException)
                {
                    // Another request may have won the create-new race.
                    existing = LoadExistingRecord(invocationId);
                    if (existing != null)
                    {
                        if (!Matches(existing, _targetId, endpoint, requestDigest))
                        {
                            return FromRecord(
                                existing,
                                InvocationClaimDisposition.Conflict,
                                "Invocation id was already used for a different target, endpoint, or exact request body.");
                        }

                        if (string.Equals(existing.state, CompletedState, StringComparison.Ordinal))
                        {
                            var replay = FromRecord(existing, InvocationClaimDisposition.Replay, "Replaying persisted response.");
                            replay.ResponseJson = existing.responseJson ?? "";
                            return replay;
                        }

                        return FromRecord(
                            existing,
                            string.Equals(existing.state, OutcomeUnknownState, StringComparison.Ordinal)
                                ? InvocationClaimDisposition.OutcomeUnknown
                                : InvocationClaimDisposition.InProgress,
                            "Invocation was already claimed.");
                    }

                    _journalWritable = false;
                    return RejectLocked(
                        invocationId,
                        endpoint,
                        requestDigest,
                        "Invocation journal claim failed; mutation was not dispatched.");
                }
                catch (Exception e)
                {
                    _journalWritable = false;
                    ConsoleLog.Warning($"Invocation journal claim failed: {e}");
                    return RejectLocked(
                        invocationId,
                        endpoint,
                        requestDigest,
                        "Invocation journal claim failed; mutation was not dispatched.");
                }

                return FromRecord(started, InvocationClaimDisposition.Execute, "Invocation claimed.");
            }
        }

        public void Maintain()
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                if (now < _nextMaintenanceUtc)
                {
                    return;
                }

                _nextMaintenanceUtc = now.AddHours(1);
                try
                {
                    var recordsHealthy = CleanupExpiredRecords();
                    _journalWritable = recordsHealthy && ProbeWritable();
#if !UNITY_EDITOR
                    CleanupExpiredPlayerState();
#endif
                }
                catch (Exception e)
                {
                    _journalWritable = false;
                    ConsoleLog.Warning($"Invocation journal maintenance failed: {e}");
                }
            }
        }

        public InvocationReceipt CreateReceipt(InvocationClaim claim, string state, bool replayed)
        {
            claim ??= new InvocationClaim();
            var protectedInvocation = !string.IsNullOrEmpty(claim.InvocationId);
            return new InvocationReceipt
            {
                invocationId = claim.InvocationId ?? "",
                targetId = string.IsNullOrEmpty(claim.TargetId) ? TargetId : claim.TargetId,
                serviceEpoch = string.IsNullOrEmpty(claim.ServiceEpoch) ? ServiceEpoch : claim.ServiceEpoch,
                endpoint = claim.Endpoint ?? "",
                requestDigest = claim.RequestDigest ?? "",
                state = protectedInvocation ? (state ?? "") : "none",
                guarantee = protectedInvocation ? "at-most-once" : "none",
                replayed = replayed,
                dedupeWindowSeconds = protectedInvocation ? DedupeWindowSeconds : 0,
                createdAtUtc = claim.CreatedAtUtc ?? "",
                updatedAtUtc = string.IsNullOrEmpty(claim.UpdatedAtUtc)
                    ? DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                    : claim.UpdatedAtUtc
            };
        }

        public bool TryComplete(InvocationClaim claim, string responseJson, out string error)
        {
            error = "";
            if (claim == null || string.IsNullOrEmpty(claim.InvocationId))
            {
                return true;
            }

            lock (_gate)
            {
                var started = LoadRecord(GetRecordPath(claim.InvocationId, StartedState));
                if (started == null || !Matches(started, claim.TargetId, claim.Endpoint, claim.RequestDigest))
                {
                    _active.Remove(claim.InvocationId);
                    var missingRecordTimestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    var syntheticStarted = new LedgerRecord
                    {
                        invocationId = claim.InvocationId,
                        targetId = claim.TargetId,
                        serviceEpoch = claim.ServiceEpoch,
                        endpoint = claim.Endpoint,
                        requestDigest = claim.RequestDigest,
                        state = StartedState,
                        createdAtUtc = string.IsNullOrEmpty(claim.CreatedAtUtc)
                            ? missingRecordTimestamp
                            : claim.CreatedAtUtc,
                        updatedAtUtc = missingRecordTimestamp
                    };
                    if (PersistOutcomeUnknown(
                        syntheticStarted,
                        "Invocation claim record disappeared after dispatch; outcome is unknown.") == null)
                    {
                        _journalWritable = false;
                    }

                    error = "Invocation claim record is missing or no longer matches.";
                    return false;
                }

                var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                var completed = CopyRecord(started);
                completed.state = CompletedState;
                completed.updatedAtUtc = now;
                completed.responseJson = responseJson ?? "";

                try
                {
                    WriteNewRecord(GetRecordPath(claim.InvocationId, CompletedState), completed);
                    claim.TerminalResponsePersisted = true;
                    _active.Remove(claim.InvocationId);
                    return true;
                }
                catch (IOException)
                {
                    var existing = LoadRecord(GetRecordPath(claim.InvocationId, CompletedState));
                    if (existing != null && Matches(existing, claim.TargetId, claim.Endpoint, claim.RequestDigest))
                    {
                        claim.TerminalResponsePersisted = true;
                        _active.Remove(claim.InvocationId);
                        return true;
                    }

                    error = "Invocation result could not be durably recorded.";
                }
                catch (Exception e)
                {
                    error = $"Invocation result could not be durably recorded: {e.Message}";
                    ConsoleLog.Warning($"Invocation completion persistence failed: {e}");
                }

                _journalWritable = false;
                PersistOutcomeUnknown(started, error);
                _active.Remove(claim.InvocationId);
                return false;
            }
        }

        public InvocationReceipt MarkOutcomeUnknown(InvocationClaim claim, string message)
        {
            if (claim == null || string.IsNullOrEmpty(claim.InvocationId))
            {
                return CreateReceipt(claim, "none", false);
            }

            lock (_gate)
            {
                var started = LoadRecord(GetRecordPath(claim.InvocationId, StartedState));
                if (started == null
                    || !Matches(started, claim.TargetId, claim.Endpoint, claim.RequestDigest))
                {
                    var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    started = new LedgerRecord
                    {
                        invocationId = claim.InvocationId,
                        targetId = claim.TargetId,
                        serviceEpoch = claim.ServiceEpoch,
                        endpoint = claim.Endpoint,
                        requestDigest = claim.RequestDigest,
                        state = StartedState,
                        createdAtUtc = string.IsNullOrEmpty(claim.CreatedAtUtc)
                            ? now
                            : claim.CreatedAtUtc,
                        updatedAtUtc = now
                    };
                }

                if (PersistOutcomeUnknown(started, message) == null)
                {
                    // Keep the live service fail-closed if the terminal marker
                    // could not be made durable. A later request must not turn a
                    // possibly executed mutation back into an executable one.
                    _journalWritable = false;
                }

                _active.Remove(claim.InvocationId);
            }

            return CreateReceipt(claim, OutcomeUnknownState, false);
        }

        public void MarkOutstandingOutcomeUnknown(string message)
        {
            lock (_gate)
            {
                foreach (var invocationId in _active.ToArray())
                {
                    var started = LoadRecord(GetRecordPath(invocationId, StartedState));
                    if (started != null)
                    {
                        PersistOutcomeUnknown(started, message);
                    }
                }

                _active.Clear();
            }
        }

        public bool TryGetStatus(
            string invocationIdText,
            string targetId,
            out InvocationStatusResponse status,
            out string error)
        {
            status = null;
            error = "";
            if (!Guid.TryParse(invocationIdText?.Trim(), out var parsedInvocationId))
            {
                error = "Invocation id must be a UUID.";
                return false;
            }

            lock (_gate)
            {
                if (!string.Equals(targetId?.Trim(), _targetId, StringComparison.Ordinal))
                {
                    error = "Target id does not match this Unity service.";
                    return false;
                }

                var invocationId = parsedInvocationId.ToString("D");
                if (HasCorruptRecord(invocationId))
                {
                    _journalWritable = false;
                    error = "Invocation journal contains a corrupt record.";
                    return false;
                }

                var record = LoadExistingRecord(invocationId);
                if (record != null
                    && !string.Equals(record.targetId, _targetId, StringComparison.Ordinal))
                {
                    error = "Invocation record belongs to a different target.";
                    return false;
                }

                var protectionExpired = false;
                var previousState = "";
                if (record != null
                    && !_active.Contains(invocationId)
                    && IsExpired(record))
                {
                    protectionExpired = true;
                    previousState = record.state ?? "";
                    if (!DeleteInvocationFiles(invocationId))
                    {
                        _journalWritable = false;
                        error = "Expired invocation records could not be removed safely.";
                        return false;
                    }
                }

                if (!protectionExpired
                    && record != null
                    && string.Equals(record.state, StartedState, StringComparison.Ordinal)
                    && !_active.Contains(invocationId))
                {
                    record = PersistOutcomeUnknown(
                        record,
                        "The service restarted before the invocation outcome was durably recorded.") ?? record;
                }

                status = new InvocationStatusResponse
                {
                    found = record != null && !protectionExpired,
                    invocationId = invocationId,
                    targetId = _targetId,
                    serviceEpoch = record?.serviceEpoch ?? _serviceEpoch,
                    endpoint = record?.endpoint ?? "",
                    requestDigest = record?.requestDigest ?? "",
                    state = protectionExpired
                        ? "protection_expired"
                        : (record == null
                            ? "not_found"
                            : (string.Equals(record.state, StartedState, StringComparison.Ordinal) ? "in_progress" : record.state)),
                    protectionExpired = protectionExpired,
                    previousState = previousState,
                    createdAtUtc = record?.createdAtUtc ?? "",
                    updatedAtUtc = record?.updatedAtUtc ?? "",
                    responseJson = string.Equals(record?.state, CompletedState, StringComparison.Ordinal)
                        ? record.responseJson ?? ""
                        : ""
                };
                return true;
            }
        }

        private void InitializeJournal()
        {
            lock (_gate)
            {
                try
                {
                    Directory.CreateDirectory(_ledgerBaseRoot);
                    var processId = Process.GetCurrentProcess().Id;
                    var processStartUtcTicks = GetProcessStartUtcTicks();
#if UNITY_EDITOR
                    var identityPath = Path.Combine(_ledgerBaseRoot, "identity.json");
#else
                    // Development Players can share persistentDataPath. Give
                    // each live process its own durable identity slot and its
                    // own target-scoped ledger so concurrent Players never
                    // race on identity.json or deduplicate each other's UUIDs.
                    var identityDirectory = Path.Combine(_ledgerBaseRoot, "identities");
                    Directory.CreateDirectory(identityDirectory);
                    var identityPath = Path.Combine(
                        identityDirectory,
                        $"{processId}-{processStartUtcTicks}.json");
#endif
                    var identity = LoadIdentity(identityPath) ?? new IdentityRecord();
                    var targetId = ResolveTargetId(
                        identity,
                        _targetId,
                        processId,
                        processStartUtcTicks);
                    // A coordinator instance is one service epoch. Domain reload
                    // constructs a new coordinator, which must be observable even
                    // when the native Unity process itself did not restart.
                    var serviceEpoch = Guid.NewGuid();

                    identity.schemaVersion = SchemaVersion;
                    identity.targetId = targetId;
#if UNITY_EDITOR
                    identity.projectRoot = ResolveEditorProjectRoot();
#else
                    identity.projectRoot = "";
#endif
                    identity.processId = processId;
                    identity.processStartUtcTicks = processStartUtcTicks;
                    identity.serviceEpoch = serviceEpoch.ToString("D");
                    WriteReplace(identityPath, JsonUtility.ToJson(identity));

                    _targetId = identity.targetId;
                    _serviceEpoch = identity.serviceEpoch;
#if !UNITY_EDITOR
                    _ledgerRoot = Path.Combine(_ledgerBaseRoot, "targets", targetId);
                    Directory.CreateDirectory(_ledgerRoot);
#endif
                    _journalWritable = ProbeWritable();

                    _journalWritable = CleanupExpiredRecords() && _journalWritable;
                    MarkOrphanedStartedRecords();
#if !UNITY_EDITOR
                    CleanupExpiredPlayerState();
#endif
                    _nextMaintenanceUtc = DateTime.UtcNow.AddHours(1);
                }
                catch (Exception e)
                {
                    try
                    {
                        _targetId = ResolveTargetId(
                            new IdentityRecord(),
                            _targetId,
                            Process.GetCurrentProcess().Id,
                            GetProcessStartUtcTicks());
                    }
                    catch
                    {
                        _targetId = "unavailable";
                    }
                    _serviceEpoch = Guid.NewGuid().ToString("D");
                    _journalWritable = false;
                    ConsoleLog.Warning($"Invocation journal initialization failed: {e}");
                }
            }
        }

        private void MarkOrphanedStartedRecords()
        {
            foreach (var path in Directory.GetFiles(_ledgerRoot, "*.started.json"))
            {
                var started = LoadRecord(path);
                if (started == null || string.IsNullOrEmpty(started.invocationId))
                {
                    continue;
                }

                if (File.Exists(GetRecordPath(started.invocationId, CompletedState))
                    || File.Exists(GetRecordPath(started.invocationId, OutcomeUnknownState)))
                {
                    continue;
                }

                if (!string.Equals(started.targetId, _targetId, StringComparison.Ordinal))
                {
                    // Multiple development Players can share persistentDataPath.
                    // A coordinator must never orphan another live target's work.
                    continue;
                }

                PersistOutcomeUnknown(
                    started,
                    "The service restarted before the invocation outcome was durably recorded.");
            }
        }

        private bool CleanupExpiredRecords()
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-DedupeWindowSeconds);
            var invocationIds = new HashSet<string>(StringComparer.Ordinal);
            var recordsHealthy = true;
            foreach (var path in Directory.GetFiles(_ledgerRoot, "*.json"))
            {
                var name = Path.GetFileName(path);
                if (string.Equals(name, "identity.json", StringComparison.Ordinal))
                {
                    continue;
                }

                var separator = name.IndexOf('.');
                if (separator > 0)
                {
                    invocationIds.Add(name.Substring(0, separator));
                }
            }

            foreach (var invocationId in invocationIds)
            {
                if (_active.Contains(invocationId))
                {
                    continue;
                }

                if (!Guid.TryParse(invocationId, out _) || HasCorruptRecord(invocationId))
                {
                    recordsHealthy = false;
                    ConsoleLog.Warning(
                        $"Corrupt invocation record retained for fail-closed recovery: {invocationId}");
                    continue;
                }

                var record = LoadExistingRecord(invocationId);
                if (record == null)
                {
                    recordsHealthy = false;
                    ConsoleLog.Warning(
                        $"Unreadable invocation record retained for fail-closed recovery: {invocationId}");
                    continue;
                }

                if (TryParseUtc(record.updatedAtUtc, out var updatedAt) && updatedAt < cutoff)
                {
                    if (!DeleteInvocationFiles(invocationId))
                    {
                        recordsHealthy = false;
                    }
                }
            }

            return recordsHealthy;
        }

        private bool HasCorruptRecord(string invocationId)
        {
            foreach (var state in new[] { StartedState, CompletedState, OutcomeUnknownState })
            {
                var path = GetRecordPath(invocationId, state);
                if (File.Exists(path) && LoadRecord(path) == null)
                {
                    return true;
                }
            }

            return false;
        }

        private LedgerRecord LoadExistingRecord(string invocationId)
        {
            var completed = LoadRecord(GetRecordPath(invocationId, CompletedState));
            var unknown = LoadRecord(GetRecordPath(invocationId, OutcomeUnknownState));
            if (completed != null && unknown != null)
            {
                // A durable completed response is stronger evidence than an
                // observer's later conservative unknown marker. The same id
                // cannot be dispatched again while either record exists.
                return completed;
            }

            return completed
                ?? unknown
                ?? LoadRecord(GetRecordPath(invocationId, StartedState));
        }

        private LedgerRecord PersistOutcomeUnknown(LedgerRecord started, string message)
        {
            if (started == null)
            {
                return null;
            }

            var existing = LoadRecord(GetRecordPath(started.invocationId, OutcomeUnknownState));
            if (existing != null)
            {
                return existing;
            }

            var unknown = CopyRecord(started);
            unknown.state = OutcomeUnknownState;
            unknown.updatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            unknown.message = message ?? "Invocation outcome is unknown.";

            try
            {
                WriteNewRecord(GetRecordPath(started.invocationId, OutcomeUnknownState), unknown);
                return unknown;
            }
            catch (IOException)
            {
                var raced = LoadRecord(GetRecordPath(started.invocationId, OutcomeUnknownState));
                if (raced == null)
                {
                    _journalWritable = false;
                }

                return raced;
            }
            catch (Exception e)
            {
                _journalWritable = false;
                ConsoleLog.Warning($"Failed to persist unknown invocation outcome: {e}");
                return null;
            }
        }

        private static LedgerRecord CopyRecord(LedgerRecord source)
        {
            return new LedgerRecord
            {
                schemaVersion = source.schemaVersion,
                invocationId = source.invocationId ?? "",
                targetId = source.targetId ?? "",
                serviceEpoch = source.serviceEpoch ?? "",
                endpoint = source.endpoint ?? "",
                requestDigest = source.requestDigest ?? "",
                state = source.state ?? "",
                createdAtUtc = source.createdAtUtc ?? "",
                updatedAtUtc = source.updatedAtUtc ?? "",
                responseJson = source.responseJson ?? "",
                message = source.message ?? ""
            };
        }

        private static bool Matches(LedgerRecord record, string targetId, string endpoint, string digest)
        {
            return record != null
                && string.Equals(record.targetId, targetId ?? "", StringComparison.Ordinal)
                && string.Equals(record.endpoint, endpoint ?? "", StringComparison.Ordinal)
                && string.Equals(record.requestDigest, digest ?? "", StringComparison.Ordinal);
        }

        private static InvocationClaim FromRecord(
            LedgerRecord record,
            InvocationClaimDisposition disposition,
            string message)
        {
            return new InvocationClaim
            {
                Disposition = disposition,
                InvocationId = record?.invocationId ?? "",
                TargetId = record?.targetId ?? "",
                ServiceEpoch = record?.serviceEpoch ?? "",
                Endpoint = record?.endpoint ?? "",
                RequestDigest = record?.requestDigest ?? "",
                ResponseJson = record?.responseJson ?? "",
                Message = message ?? "",
                CreatedAtUtc = record?.createdAtUtc ?? "",
                UpdatedAtUtc = record?.updatedAtUtc ?? ""
            };
        }

        private InvocationClaim Reject(string invocationId, string targetId, string endpoint, string message)
        {
            lock (_gate)
            {
                return new InvocationClaim
                {
                    Disposition = InvocationClaimDisposition.Rejected,
                    InvocationId = invocationId ?? "",
                    TargetId = string.IsNullOrEmpty(_targetId) ? targetId ?? "" : _targetId,
                    ServiceEpoch = _serviceEpoch,
                    Endpoint = endpoint ?? "",
                    Message = message ?? ""
                };
            }
        }

        private InvocationClaim RejectLocked(string invocationId, string endpoint, string digest, string message)
        {
            return new InvocationClaim
            {
                Disposition = InvocationClaimDisposition.Rejected,
                InvocationId = invocationId ?? "",
                TargetId = _targetId,
                ServiceEpoch = _serviceEpoch,
                Endpoint = endpoint ?? "",
                RequestDigest = digest ?? "",
                Message = message ?? ""
            };
        }

        private bool IsExpired(LedgerRecord record)
        {
            return TryParseUtc(record?.updatedAtUtc, out var updatedAt)
                && updatedAt < DateTime.UtcNow.AddSeconds(-DedupeWindowSeconds);
        }

        private bool DeleteInvocationFiles(string invocationId)
        {
            var allDeleted = true;
            foreach (var state in new[] { StartedState, CompletedState, OutcomeUnknownState })
            {
                var path = GetRecordPath(invocationId, state);
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception e)
                {
                    allDeleted = false;
                    ConsoleLog.Debug($"Failed to delete expired invocation record {path}: {e.Message}");
                }
            }

            return allDeleted;
        }

        private string GetRecordPath(string invocationId, string state)
        {
            return Path.Combine(_ledgerRoot, $"{invocationId}.{state}.json");
        }

        private static LedgerRecord LoadRecord(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                var json = File.ReadAllText(path);
                var record = string.IsNullOrWhiteSpace(json)
                    ? null
                    : JsonUtility.FromJson<LedgerRecord>(json);
                if (record == null
                    || record.schemaVersion != SchemaVersion
                    || !Guid.TryParse(record.invocationId, out var parsedInvocationId))
                {
                    return null;
                }

                record.invocationId = parsedInvocationId.ToString("D");
                var expectedPrefix = record.invocationId + ".";
                var fileName = Path.GetFileName(path);
                if (!fileName.StartsWith(expectedPrefix, StringComparison.Ordinal))
                {
                    ConsoleLog.Warning($"Invocation record filename does not match its id: {path}");
                    return null;
                }

                var expectedState = fileName.EndsWith($".{StartedState}.json", StringComparison.Ordinal)
                    ? StartedState
                    : (fileName.EndsWith($".{CompletedState}.json", StringComparison.Ordinal)
                        ? CompletedState
                        : (fileName.EndsWith($".{OutcomeUnknownState}.json", StringComparison.Ordinal)
                            ? OutcomeUnknownState
                            : ""));
                if (string.IsNullOrEmpty(expectedState)
                    || !string.Equals(record.state, expectedState, StringComparison.Ordinal)
                    || string.IsNullOrEmpty(record.targetId)
                    || !Guid.TryParse(record.serviceEpoch, out _)
                    || string.IsNullOrEmpty(record.endpoint)
                    || !IsSha256Hex(record.requestDigest)
                    || !TryParseUtc(record.createdAtUtc, out _)
                    || !TryParseUtc(record.updatedAtUtc, out _))
                {
                    ConsoleLog.Warning($"Invocation record failed structural validation: {path}");
                    return null;
                }

                if (string.Equals(record.state, CompletedState, StringComparison.Ordinal)
                    && !IsResponseEnvelopeJson(record.responseJson))
                {
                    ConsoleLog.Warning($"Completed invocation response is unreadable: {path}");
                    return null;
                }

                return record;
            }
            catch (Exception e)
            {
                ConsoleLog.Warning($"Failed to read invocation record {path}: {e.Message}");
                return null;
            }
        }

        private static IdentityRecord LoadIdentity(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                var json = File.ReadAllText(path);
                return string.IsNullOrWhiteSpace(json) ? null : JsonUtility.FromJson<IdentityRecord>(json);
            }
            catch (Exception e)
            {
                ConsoleLog.Warning($"Failed to read invocation identity: {e.Message}");
                return null;
            }
        }

        private static void WriteNewRecord(string path, LedgerRecord record)
        {
            var bytes = new UTF8Encoding(false).GetBytes(JsonUtility.ToJson(record));
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        private static void WriteReplace(string path, string contents)
        {
            var tempPath = path + $".tmp.{Guid.NewGuid():N}";
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(contents ?? "");
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tempPath, path, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Delete(path);
                        File.Move(tempPath, path);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                        // Best effort cleanup.
                    }
                }
            }
        }

        private bool ProbeWritable()
        {
            var path = Path.Combine(_ledgerRoot, $".probe.{Guid.NewGuid():N}");
            try
            {
                File.WriteAllText(path, "ok", new UTF8Encoding(false));
                File.Delete(path);
                return true;
            }
            catch (Exception e)
            {
                ConsoleLog.Warning($"Invocation journal is not writable: {e.Message}");
                return false;
            }
        }

        private static string ResolveLedgerRoot()
        {
#if UNITY_EDITOR
            return Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "Library", "CSharpConsole", "InvocationLedger", "v1"));
#else
            return Path.GetFullPath(
                Path.Combine(Application.persistentDataPath, "CSharpConsole", "InvocationLedger", "v1"));
#endif
        }

        private static string ResolveTargetId(
            IdentityRecord identity,
            string currentTargetId,
            int currentProcessId,
            long currentProcessStartUtcTicks)
        {
#if UNITY_EDITOR
            var projectRoot = ResolveEditorProjectRoot();
            var bytes = new UTF8Encoding(false).GetBytes(projectRoot);
            using var sha256 = SHA256.Create();
            var digest = sha256.ComputeHash(bytes);
            var builder = new StringBuilder(24);
            for (var index = 0; index < 12; index++)
            {
                builder.Append(digest[index].ToString("x2", CultureInfo.InvariantCulture));
            }

            return "editor-" + builder;
#else
            if (IsPlayerTargetId(currentTargetId))
            {
                // A service restart in the same process keeps its target.
                return NormalizePlayerTargetId(currentTargetId);
            }

            var persistedTargetId = identity?.targetId ?? "";
            var sameProcess = identity != null
                && identity.processId == currentProcessId
                && identity.processStartUtcTicks == currentProcessStartUtcTicks;
            if (!string.IsNullOrEmpty(persistedTargetId)
                && persistedTargetId.StartsWith("player-", StringComparison.Ordinal)
                && Guid.TryParse(persistedTargetId.Substring("player-".Length), out var persistedPlayerId)
                && sameProcess)
            {
                return "player-" + persistedPlayerId.ToString("D");
            }

            return "player-" + Guid.NewGuid().ToString("D");
#endif
        }

#if UNITY_EDITOR
        private static string ResolveEditorProjectRoot()
        {
            var projectRoot = Path
                .GetFullPath(Path.Combine(Application.dataPath, ".."))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Path.DirectorySeparatorChar == '\\')
            {
                projectRoot = projectRoot.ToLowerInvariant();
            }

            return projectRoot.Replace('\\', '/');
        }
#endif

#if !UNITY_EDITOR
        private static bool IsPlayerTargetId(string value)
        {
            return !string.IsNullOrEmpty(value)
                && value.StartsWith("player-", StringComparison.Ordinal)
                && Guid.TryParse(value.Substring("player-".Length), out _);
        }

        private static string NormalizePlayerTargetId(string value)
        {
            return "player-" + Guid.Parse(value.Substring("player-".Length)).ToString("D");
        }

        private void CleanupExpiredPlayerState()
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddSeconds(-DedupeWindowSeconds);
                var identitiesRoot = Path.Combine(_ledgerBaseRoot, "identities");
                var targetsRoot = Path.Combine(_ledgerBaseRoot, "targets");
                var liveTargets = new HashSet<string>(StringComparer.Ordinal);

                if (Directory.Exists(identitiesRoot))
                {
                    foreach (var identityPath in Directory.GetFiles(identitiesRoot, "*.json"))
                    {
                        var identity = LoadIdentity(identityPath);
                        if (identity == null || !IsPlayerTargetId(identity.targetId))
                        {
                            // Retain unreadable identity state for manual recovery.
                            continue;
                        }

                        var targetId = NormalizePlayerTargetId(identity.targetId);
                        if (string.Equals(targetId, _targetId, StringComparison.Ordinal)
                            || IsProcessInstanceAlive(
                                identity.processId,
                                identity.processStartUtcTicks))
                        {
                            liveTargets.Add(targetId);
                        }
                    }
                }

                if (Directory.Exists(targetsRoot))
                {
                    foreach (var targetDirectory in Directory.GetDirectories(targetsRoot))
                    {
                        var targetId = Path.GetFileName(targetDirectory);
                        if (!IsPlayerTargetId(targetId) || liveTargets.Contains(targetId))
                        {
                            continue;
                        }

                        CleanupExpiredPlayerTargetDirectory(targetDirectory, cutoff);
                    }
                }

                if (!Directory.Exists(identitiesRoot))
                {
                    return;
                }

                foreach (var identityPath in Directory.GetFiles(identitiesRoot, "*.json"))
                {
                    try
                    {
                        var identity = LoadIdentity(identityPath);
                        if (identity == null || !IsPlayerTargetId(identity.targetId))
                        {
                            continue;
                        }

                        var targetId = NormalizePlayerTargetId(identity.targetId);
                        if (liveTargets.Contains(targetId)
                            || File.GetLastWriteTimeUtc(identityPath) >= cutoff)
                        {
                            continue;
                        }

                        var targetDirectory = Path.Combine(targetsRoot, targetId);
                        if (!Directory.Exists(targetDirectory))
                        {
                            File.Delete(identityPath);
                        }
                    }
                    catch (Exception e)
                    {
                        ConsoleLog.Debug(
                            $"Failed to clean expired Player invocation identity {identityPath}: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                // Cross-target cleanup is never part of the current target's
                // at-most-once write boundary.
                ConsoleLog.Debug($"Failed to inspect expired Player invocation state: {e.Message}");
            }
        }

        private static void CleanupExpiredPlayerTargetDirectory(
            string targetDirectory,
            DateTime cutoff)
        {
            try
            {
                foreach (var recordPath in Directory.GetFiles(targetDirectory, "*.json"))
                {
                    var record = LoadRecord(recordPath);
                    if (record != null
                        && TryParseUtc(record.updatedAtUtc, out var updatedAt)
                        && updatedAt < cutoff)
                    {
                        File.Delete(recordPath);
                    }
                }

                if (Directory.GetFileSystemEntries(targetDirectory).Length == 0)
                {
                    Directory.Delete(targetDirectory, false);
                }
            }
            catch (Exception e)
            {
                // Cross-target cleanup is best effort and must never make the
                // current live target's otherwise healthy journal unavailable.
                ConsoleLog.Debug(
                    $"Failed to clean expired Player invocation target {targetDirectory}: {e.Message}");
            }
        }

        private static bool IsProcessInstanceAlive(int processId, long expectedStartUtcTicks)
        {
            if (processId <= 0)
            {
                return false;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return false;
                }

                if (expectedStartUtcTicks <= 0)
                {
                    return true;
                }

                return process.StartTime.ToUniversalTime().Ticks == expectedStartUtcTicks;
            }
            catch
            {
                return false;
            }
        }

#endif

        private static long GetProcessStartUtcTicks()
        {
            try
            {
                return Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
            }
            catch
            {
                // The process id still prevents accidental identity reuse in the
                // common case; this sentinel is stable across domain reloads.
                return 0;
            }
        }

        private static string ComputeDigest(byte[] body)
        {
            using var sha256 = SHA256.Create();
            var digest = sha256.ComputeHash(body ?? Array.Empty<byte>());
            var builder = new StringBuilder(digest.Length * 2);
            foreach (var value in digest)
            {
                builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static bool TryParseUtc(string text, out DateTime value)
        {
            return DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out value);
        }

        private static bool IsSha256Hex(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64)
            {
                return false;
            }

            foreach (var character in value)
            {
                if (!Uri.IsHexDigit(character))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsResponseEnvelopeJson(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson)
                || responseJson.IndexOf("\"dataJson\"", StringComparison.Ordinal) < 0
                || responseJson.IndexOf("\"summary\"", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            try
            {
                return JsonUtility.FromJson<HttpResponseEnvelope>(responseJson) != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
