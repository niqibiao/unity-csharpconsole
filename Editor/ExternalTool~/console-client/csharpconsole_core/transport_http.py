import json as _json
import urllib.error
import urllib.request


class TransportError(Exception):
    """Raised when an HTTP request to the console service fails at the transport layer."""


def _post(url, data=None, json=None, content_type="application/json", timeout_seconds=30):
    if json is not None:
        body = _json.dumps(json).encode("utf-8")
    elif data is None:
        body = b""
    elif isinstance(data, (bytes, bytearray)):
        body = bytes(data)
    else:
        body = str(data).encode("utf-8")

    request = urllib.request.Request(
        url,
        data=body,
        headers={"Content-Type": content_type},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            charset = response.headers.get_content_charset() or "utf-8"
            return response.read().decode(charset)
    except urllib.error.HTTPError as e:
        # urlopen raises for non-2xx, mirroring requests' raise_for_status().
        raise TransportError(f"HTTP {e.code} {e.reason}") from e
    except OSError as e:
        # URLError (connection refused / DNS failure), socket timeout, etc.
        raise TransportError(str(getattr(e, "reason", e))) from e


def post_json(server_base_url, endpoint, payload, timeout_seconds):
    url = f"{server_base_url}/{endpoint}"
    return _post(url, json=payload, timeout_seconds=timeout_seconds)


def post_json_to_execute(execute_base_url, payload, timeout_seconds):
    url = f"{execute_base_url}/execute"
    return _post(url, json=payload, timeout_seconds=timeout_seconds)


def post_binary(url, body, timeout_seconds):
    return _post(url, data=body, content_type="application/octet-stream", timeout_seconds=timeout_seconds)
