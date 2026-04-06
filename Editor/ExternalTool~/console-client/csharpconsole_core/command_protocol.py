import json
import time

import requests

from .models import make_result, new_run_id


def _coerce_args_json(raw_args):
    if raw_args is None:
        return "{}"
    if isinstance(raw_args, str):
        raw = raw_args.strip()
        if not raw:
            return "{}"
        try:
            parsed = json.loads(raw)
            return json.dumps(parsed, ensure_ascii=False)
        except Exception:
            return json.dumps({"value": raw}, ensure_ascii=False)

    if isinstance(raw_args, dict):
        return json.dumps(raw_args, ensure_ascii=False)

    if isinstance(raw_args, (list, tuple)):
        if len(raw_args) == 0:
            return "{}"
        if len(raw_args) == 1:
            only = raw_args[0]
            if isinstance(only, str):
                text = only.strip()
                if not text:
                    return "{}"
                try:
                    parsed = json.loads(text)
                    return json.dumps(parsed, ensure_ascii=False)
                except Exception:
                    return json.dumps({"value": text}, ensure_ascii=False)
            if isinstance(only, dict):
                return json.dumps(only, ensure_ascii=False)
        return json.dumps({"argv": list(raw_args)}, ensure_ascii=False)

    return json.dumps({"value": raw_args}, ensure_ascii=False)


def request_command(post_json_func, parse_command_http_response, current_mode_name, command_namespace, action, session_id="", raw_args=None, timeout_seconds=10):
    start = time.time()
    run_id = new_run_id()
    args_json = _coerce_args_json(raw_args)
    payload = {
        "invocation": {
            "source": "python-repl",
            "requestedCapability": "command",
            "sessionId": session_id or "",
            "command": {
                "commandNamespace": command_namespace or "",
                "action": action or "",
            },
            "argsJson": args_json,
        },
    }

    try:
        raw = post_json_func("command", payload, timeout_seconds)
        return parse_command_http_response(
            raw,
            session_id=session_id,
            mode=current_mode_name(),
            run_id=run_id,
            duration_ms=(time.time() - start) * 1000,
        )
    except requests.RequestException as e:
        return make_result(
            ok=False,
            stage="command",
            result_type="system_error",
            exit_code=3,
            summary=f"Command request failed: {e}",
            session_id=session_id,
            mode=current_mode_name(),
            run_id=run_id,
            duration_ms=(time.time() - start) * 1000,
        )
    except Exception as e:
        return make_result(
            ok=False,
            stage="command",
            result_type="system_error",
            exit_code=3,
            summary=str(e),
            session_id=session_id,
            mode=current_mode_name(),
            run_id=run_id,
            duration_ms=(time.time() - start) * 1000,
        )
