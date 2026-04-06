import json

from .models import make_result


def _try_load_json_object(raw):
    if isinstance(raw, dict):
        return raw
    if not isinstance(raw, str):
        return None

    text = raw.strip()
    if not text:
        return None

    try:
        data = json.loads(text)
    except Exception:
        return None
    return data if isinstance(data, dict) else None


def _decode_data_json(raw_data):
    if isinstance(raw_data, dict):
        return raw_data
    if raw_data is None:
        return {}
    if isinstance(raw_data, str):
        text = raw_data.strip()
        if not text:
            return {}
        try:
            parsed = json.loads(text)
        except Exception:
            return {"text": raw_data}
        return parsed if isinstance(parsed, dict) else {"value": parsed}
    return {"value": raw_data}


def _exit_code_from_type(ok, result_type):
    if ok:
        return 0
    if result_type in {"validation_error", "compile_error"}:
        return 1
    if result_type == "runtime_error":
        return 2
    return 3


def _is_response_envelope(data):
    return isinstance(data, dict) and "ok" in data and "summary" in data and "dataJson" in data


def _build_envelope_result(envelope, default_stage, session_id, mode, run_id, duration_ms):
    ok = bool(envelope.get("ok"))
    stage = envelope.get("stage") or default_stage
    result_type = envelope.get("type", "") if ok else (envelope.get("type") or "system_error")
    summary = envelope.get("summary") or ("OK" if ok else "Request failed")
    resolved_session_id = envelope.get("sessionId", session_id) or session_id
    data = _decode_data_json(envelope.get("dataJson"))
    return make_result(
        ok,
        stage,
        result_type,
        _exit_code_from_type(ok, result_type),
        summary,
        resolved_session_id,
        mode,
        run_id,
        duration_ms,
        data,
    )


def _parse_envelope_result(raw, default_stage, session_id, mode, run_id, duration_ms):
    data = _try_load_json_object(raw)
    if not _is_response_envelope(data):
        return None
    return _build_envelope_result(data, default_stage, session_id, mode, run_id, duration_ms)


def _extract_text_from_data(data):
    if not isinstance(data, dict):
        return None
    if "text" in data and isinstance(data.get("text"), str):
        return data["text"]
    result = data.get("result")
    if isinstance(result, str):
        return result
    error = data.get("error")
    if isinstance(error, str):
        return error
    return None


def classify_response_text(text, default_stage):
    text = (text or "").strip()
    if not text:
        return True, default_stage, "", 0, "OK"

    lowered = text.lower()
    if lowered.startswith("compile failed"):
        return False, "compile", "compile_error", 1, text
    if "forward failed" in lowered:
        return False, "execute", "runtime_error", 2, text
    if lowered.startswith("timeout:"):
        return False, "execute", "runtime_error", 2, text
    if "error post:" in lowered:
        return False, "unknown", "system_error", 3, text
    if "exception" in lowered or "load error:" in lowered or "execution error:" in lowered:
        return False, default_stage, "runtime_error", 2, text
    return True, default_stage, "", 0, text


def parse_text_http_response(raw, default_stage, session_id, mode, run_id, duration_ms):
    result = _parse_envelope_result(raw, default_stage, session_id, mode, run_id, duration_ms)
    if result is not None:
        text = _extract_text_from_data(result.get("data", {}))
        if text is not None:
            result["data"] = {**result.get("data", {}), "text": text}
        return result

    text = raw or ""
    ok, stage, result_type, exit_code, summary = classify_response_text(text, default_stage)
    return make_result(ok, stage, result_type, exit_code, summary, session_id, mode, run_id, duration_ms, {"text": text})


def parse_compile_only_http_response(raw, session_id, mode, run_id, duration_ms):
    result = _parse_envelope_result(raw, "compile", session_id, mode, run_id, duration_ms)
    if result is not None:
        return result

    data = _try_load_json_object(raw)
    if not isinstance(data, dict):
        raise ValueError("Invalid compile response")
    if data.get("error"):
        return make_result(False, "compile", "compile_error", 1, data["error"], session_id, mode, run_id, duration_ms, data)
    return make_result(True, "compile", "", 0, "Compile succeeded", session_id, mode, run_id, duration_ms, data)


def parse_completion_http_response(raw, session_id, mode, run_id, duration_ms):
    result = _parse_envelope_result(raw, "compile", session_id, mode, run_id, duration_ms)
    if result is not None:
        return result

    data = _try_load_json_object(raw)
    if not isinstance(data, dict):
        raise ValueError("Invalid completion response")
    if data.get("error"):
        return make_result(False, "compile", "runtime_error", 2, data["error"], session_id, mode, run_id, duration_ms, data)
    return make_result(True, "compile", "", 0, f"Returned {len(data.get('items', []))} completion items", session_id, mode, run_id, duration_ms, data)


def parse_health_http_response(raw, mode, run_id, duration_ms):
    result = _parse_envelope_result(raw, "bootstrap", "", mode, run_id, duration_ms)
    if result is not None:
        return result

    data = _try_load_json_object(raw)
    if not isinstance(data, dict):
        raise ValueError("Invalid health response")
    return make_result(True, "bootstrap", "", 0, "Service is healthy", "", mode, run_id, duration_ms, data)


def parse_refresh_http_response(raw, mode, run_id, duration_ms):
    result = _parse_envelope_result(raw, "bootstrap", "", mode, run_id, duration_ms)
    if result is not None:
        return result

    data = _try_load_json_object(raw)
    if not isinstance(data, dict):
        raise ValueError("Invalid refresh response")
    ok = bool(data.get("ok"))
    accepted = bool(data.get("accepted"))
    summary = data.get("message") or ("Refresh accepted" if accepted else "Refresh was not accepted")
    result_type = "" if ok else "system_error"
    return make_result(ok, "bootstrap", result_type, _exit_code_from_type(ok, result_type), summary, "", mode, run_id, duration_ms, data)


def parse_command_http_response(raw, session_id, mode, run_id, duration_ms):
    result = _parse_envelope_result(raw, "command", session_id, mode, run_id, duration_ms)
    if result is None:
        raise ValueError("Invalid command response")

    data = result.get("data", {})
    command_payload = data if isinstance(data, dict) else {}
    if "command" not in command_payload or "resultJson" not in command_payload:
        raise ValueError("Invalid command response")

    parsed_result_json = command_payload.get("resultJson")
    if isinstance(parsed_result_json, str) and parsed_result_json.strip():
        try:
            parsed_result_json = json.loads(parsed_result_json)
        except Exception:
            pass
    result["data"] = {
        "command": command_payload.get("command") or {},
        "resultJson": parsed_result_json,
        "nextAction": command_payload.get("nextAction", ""),
    }
    return result


def parse_upload_dlls_http_response(raw, mode, run_id, duration_ms):
    result = _parse_envelope_result(raw, "bootstrap", "", mode, run_id, duration_ms)
    if result is not None:
        if not result["ok"]:
            raise RuntimeError(result["summary"])
        data = result.get("data", {})
        if not isinstance(data, dict):
            raise RuntimeError("upload-dlls failed: invalid response payload")
        return data

    data = _try_load_json_object(raw)
    if not isinstance(data, dict):
        raise RuntimeError("upload-dlls failed: invalid response payload")
    if data.get("error"):
        raise RuntimeError(f"upload-dlls failed: {data['error']}")
    return data
