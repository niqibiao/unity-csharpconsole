import json
import unittest
from pathlib import Path

import _bootstrap  # noqa: F401
from csharpconsole_core.command_protocol import _coerce_args_json, request_command
from csharpconsole_core.transport_http import TransportError


REPO_ROOT = Path(__file__).resolve().parents[5]


class CommandProtocolTests(unittest.TestCase):
    def test_coerce_args_json_from_single_json_string(self):
        payload = _coerce_args_json(["{\"foo\":1}"])
        self.assertEqual(json.loads(payload), {"foo": 1})

    def test_coerce_args_json_wraps_plain_string(self):
        payload = _coerce_args_json("hello")
        self.assertEqual(json.loads(payload), {"value": "hello"})

    def test_coerce_args_json_accepts_dict(self):
        payload = _coerce_args_json({"foo": 1})
        self.assertEqual(json.loads(payload), {"foo": 1})

    def test_request_command_sends_structured_invocation_payload(self):
        captured = {}

        def post_json_func(endpoint, payload, timeout_seconds):
            captured["endpoint"] = endpoint
            captured["payload"] = payload
            return json.dumps({
                "ok": True,
                "stage": "command",
                "type": "ok",
                "summary": "listed",
                "sessionId": "sid-1",
                "dataJson": json.dumps({
                    "command": {
                        "commandNamespace": "session",
                        "action": "list",
                    },
                    "resultJson": {"items": []},
                }),
            })

        def current_mode_name():
            return "editor"

        from csharpconsole_core.response_parser import parse_command_http_response

        result = request_command(
            post_json_func,
            parse_command_http_response,
            current_mode_name,
            "session",
            "list",
            session_id="sid-1",
            raw_args={"all": True},
        )

        self.assertTrue(result["ok"])
        self.assertEqual(captured["endpoint"], "command")
        self.assertEqual(sorted(captured["payload"].keys()), ["invocation"])
        self.assertEqual(captured["payload"]["invocation"]["command"]["commandNamespace"], "session")
        self.assertEqual(captured["payload"]["invocation"]["command"]["action"], "list")
        self.assertEqual(captured["payload"]["invocation"]["sessionId"], "sid-1")
        self.assertEqual(json.loads(captured["payload"]["invocation"]["argsJson"]), {"all": True})

    def test_request_command_maps_request_exception(self):
        def post_json_func(endpoint, payload, timeout_seconds):
            raise TransportError("boom")

        def current_mode_name():
            return "editor"

        from csharpconsole_core.response_parser import parse_command_http_response
        result = request_command(post_json_func, parse_command_http_response, current_mode_name, "session", "list")
        self.assertFalse(result["ok"])
        self.assertEqual(result["type"], "system_error")
        self.assertEqual(result["exitCode"], 3)


if __name__ == "__main__":
    unittest.main()


class RuntimeStructureTests(unittest.TestCase):
    def test_command_router_no_longer_uses_response_factory_from_result(self):
        router_path = REPO_ROOT / "Runtime/Service/Commands/Routing/CommandRouter.cs"
        router_source = router_path.read_text(encoding="utf-8")
        self.assertNotIn("CommandResponseFactory.FromResult(", router_source)

    def test_command_router_no_longer_requires_context_only_signature(self):
        router_path = REPO_ROOT / "Runtime/Service/Commands/Routing/CommandRouter.cs"
        router_source = router_path.read_text(encoding="utf-8")
        self.assertNotIn("CommandResponse Handler(CommandActionContext)", router_source)

    def test_runtime_command_flow_no_longer_contains_command_result_type(self):
        command_result_path = REPO_ROOT / "Runtime/Service/Commands/Core/CommandResult.cs"
        self.assertFalse(command_result_path.exists())

    def test_runtime_command_flow_removes_command_action_context_type(self):
        context_path = REPO_ROOT / "Runtime/Service/Commands/Core/CommandActionContext.cs"
        self.assertFalse(context_path.exists())

    def test_runtime_command_flow_adds_argument_binder(self):
        binder_path = REPO_ROOT / "Runtime/Service/Commands/Core/CommandArgumentBinder.cs"
        self.assertTrue(binder_path.exists())

    def test_command_handlers_no_longer_use_context_parse_pattern(self):
        handlers_dir = REPO_ROOT / "Runtime/Service/Commands/Handlers"
        source = "\n".join(
            path.read_text(encoding="utf-8")
            for path in handlers_dir.glob("*CommandActions.cs")
        )
        self.assertNotIn("TryParseArgs(", source)
        self.assertNotIn("argsType: typeof(", source)
        self.assertNotIn("CommandActionContext context", source)

    def test_runtime_command_flow_removes_command_args_parser(self):
        parser_path = REPO_ROOT / "Runtime/Service/Commands/Core/CommandArgsParser.cs"
        self.assertFalse(parser_path.exists())
