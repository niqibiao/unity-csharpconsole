import json as _json
import urllib.error
import urllib.request


class TransportError(Exception):
    """Raised when an HTTP request to the console service fails at the transport layer."""


def encode_json_body(payload):
    """Serialize a JSON payload to deterministic UTF-8 bytes."""
    return _json.dumps(
        payload,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
        allow_nan=False,
    ).encode("utf-8")


def _coerce_body_bytes(body):
    if isinstance(body, bytes):
        return body
    if isinstance(body, (bytearray, memoryview)):
        return bytes(body)
    if isinstance(body, str):
        return body.encode("utf-8")
    raise TypeError("HTTP request body must be bytes or a string")


def _post(
    url,
    data=None,
    json=None,
    content_type="application/json",
    timeout_seconds=30,
    *,
    headers=None,
    body=None,
):
    if body is not None:
        request_body = _coerce_body_bytes(body)
    elif json is not None:
        request_body = encode_json_body(json)
    elif data is None:
        request_body = b""
    elif isinstance(data, (bytes, bytearray)):
        request_body = bytes(data)
    else:
        request_body = str(data).encode("utf-8")

    request_headers = {"Content-Type": content_type}
    if headers:
        request_headers.update(headers)

    request = urllib.request.Request(
        url,
        data=request_body,
        headers=request_headers,
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            try:
                charset = response.headers.get_content_charset() or "utf-8"
                return response.read().decode(charset)
            except Exception as e:
                # urlopen has already dispatched the request at this point.
                # IncompleteRead/HTTPException, malformed charset metadata, and
                # decode failures must all cross the reliable caller's
                # post-send uncertainty boundary as TransportError.
                raise TransportError(
                    f"Failed to read HTTP response: {e}"
                ) from e
    except urllib.error.HTTPError as e:
        # urlopen raises for non-2xx, mirroring requests' raise_for_status().
        raise TransportError(f"HTTP {e.code} {e.reason}") from e
    except OSError as e:
        # URLError (connection refused / DNS failure), socket timeout, etc.
        raise TransportError(str(getattr(e, "reason", e))) from e


def post_json(
    server_base_url,
    endpoint,
    payload=None,
    timeout_seconds=30,
    *,
    headers=None,
    body=None,
):
    url = f"{server_base_url}/{endpoint}"
    return _post(
        url,
        json=payload if body is None else None,
        timeout_seconds=timeout_seconds,
        headers=headers,
        body=body,
    )


def post_json_to_execute(
    execute_base_url,
    payload=None,
    timeout_seconds=30,
    *,
    headers=None,
    body=None,
):
    url = f"{execute_base_url}/execute"
    return _post(
        url,
        json=payload if body is None else None,
        timeout_seconds=timeout_seconds,
        headers=headers,
        body=body,
    )


def post_binary(url, body, timeout_seconds, *, headers=None):
    return _post(
        url,
        body=body,
        content_type="application/octet-stream",
        timeout_seconds=timeout_seconds,
        headers=headers,
    )
