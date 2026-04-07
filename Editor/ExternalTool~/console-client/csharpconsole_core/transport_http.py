import requests


def _post(url, data=None, json=None, content_type="application/json", timeout_seconds=30):
    headers = {"Content-Type": content_type}
    response = requests.post(url, data=data, json=json, headers=headers, timeout=timeout_seconds)
    response.raise_for_status()
    return response.text


def post_json(server_base_url, endpoint, payload, timeout_seconds):
    url = f"{server_base_url}/{endpoint}"
    return _post(url, json=payload, timeout_seconds=timeout_seconds)


def post_json_to_execute(execute_base_url, payload, timeout_seconds):
    url = f"{execute_base_url}/execute"
    return _post(url, json=payload, timeout_seconds=timeout_seconds)


def post_binary(url, body, timeout_seconds):
    return _post(url, data=body, content_type="application/octet-stream", timeout_seconds=timeout_seconds)
