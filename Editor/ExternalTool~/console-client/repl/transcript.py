from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, Dict, List


@dataclass
class TranscriptEntry:
    entry_type: str
    text: str = ""
    ok: bool = True
    stage: str = ""
    result_type: str = ""
    error_kind: str = ""
    summary: str = ""
    payload: Dict[str, Any] = field(default_factory=dict)
    created_at: str = field(
        default_factory=lambda: datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")
    )


class TranscriptState:
    def __init__(self):
        self.entries: List[TranscriptEntry] = []

    def append(self, entry: TranscriptEntry):
        self.entries.append(entry)
        return entry

    def append_input(self, text):
        return self.append(TranscriptEntry(entry_type="input", text=text or ""))

    def append_info(self, text, payload=None):
        return self.append(TranscriptEntry(entry_type="info", text=text or "", payload=payload or {}))

    def append_result(self, entry: TranscriptEntry):
        if entry.entry_type != "result":
            raise ValueError("append_result expects a result transcript entry")
        return self.append(entry)

    def clear(self):
        self.entries.clear()
