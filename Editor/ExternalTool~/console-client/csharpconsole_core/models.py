import uuid
from datetime import datetime


def new_run_id():
    return datetime.now().strftime("%Y%m%d-%H%M%S-") + uuid.uuid4().hex[:8]


def make_result(ok, stage, result_type, exit_code, summary, session_id, mode, run_id=None, duration_ms=0, data=None):
    return {
        "ok": ok,
        "stage": stage,
        "type": result_type,
        "exitCode": exit_code,
        "summary": summary,
        "durationMs": int(duration_ms),
        "sessionId": session_id,
        "runId": run_id or new_run_id(),
        "mode": mode,
        "data": data or {},
    }
