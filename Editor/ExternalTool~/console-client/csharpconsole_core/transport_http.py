import requests


def post_json(server_base_url, endpoint, payload, timeout_seconds):
    url = f"{server_base_url}/{endpoint}"
    response = requests.post(url, json=payload, headers={"Content-Type": "application/json"}, timeout=timeout_seconds)
    response.raise_for_status()
    return response.text


def post_json_to_execute(execute_base_url, payload, timeout_seconds):
    url = f"{execute_base_url}/execute"
    response = requests.post(url, json=payload, headers={"Content-Type": "application/json"}, timeout=timeout_seconds)
    response.raise_for_status()
    return response.text


def post_binary(url, body, timeout_seconds):
    response = requests.post(url, data=body, headers={"Content-Type": "application/octet-stream"}, timeout=timeout_seconds)
    response.raise_for_status()
    return response.text
