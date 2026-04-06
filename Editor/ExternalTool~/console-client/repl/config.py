import os

from csharpconsole_core.config_base import SharedConfigState, add_common_connection_args as add_common_connection_args_base, configure_shared_globals, make_cache_paths

_script_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
locals().update(make_cache_paths(_script_dir))

_DEFAULT_USING_PREFIX_CACHE = None
_DEFAULT_DEFINE_CACHE = None
_RUNTIME_DEFINE_LINE_CACHE = None
_RUNTIME_DEFINE_LINE_OVERRIDE = None

_RUNTIME_DEFINES_FILE_NAME = "runtime-defines.txt"
_RUNTIME_DEFINES_LOG_PREFIX = "[CSharpConsole][RuntimeDefines]"

DEFAULT_LOOPBACK_HOST = "127.0.0.1"
DEFAULT_EDITOR_PORT = 14500

_state = SharedConfigState()
ip = _state.ip
port = _state.port
runtime_mode = _state.runtime_mode
compile_ip = _state.compile_ip
compile_port = _state.compile_port
runtime_ip = _state.runtime_ip
runtime_port = _state.runtime_port
runtime_dll_path = _state.runtime_dll_path
runtime_defines_path = ""
parsed_args = _state.parsed_args


def add_common_connection_args(parser):
    add_common_connection_args_base(parser, lambda p: p.add_argument('--runtime-defines', default=''))


def normalize_argv(argv, default_command='repl'):
    subcommands = {'repl', 'run', 'compile', 'execute', 'reset', 'completion', 'upload-dlls', 'health', 'refresh'}
    if len(argv) > 1 and argv[1] in subcommands:
        return argv
    return [argv[0], default_command, *argv[1:]]


def configure_globals(args):
    global ip, port, runtime_mode, compile_ip, compile_port, runtime_ip, runtime_port, runtime_dll_path, runtime_defines_path, parsed_args, _RUNTIME_DEFINE_LINE_OVERRIDE
    runtime_defines_path = args.runtime_defines
    _RUNTIME_DEFINE_LINE_OVERRIDE = None
    configure_shared_globals(_state, args)
    parsed_args = _state.parsed_args
    ip = _state.ip
    port = _state.port
    runtime_mode = _state.runtime_mode
    compile_ip = _state.compile_ip
    compile_port = _state.compile_port
    runtime_ip = _state.runtime_ip
    runtime_port = _state.runtime_port
    runtime_dll_path = _state.runtime_dll_path


def reset_cached_config():
    global _DEFAULT_USING_PREFIX_CACHE, _DEFAULT_DEFINE_CACHE, _RUNTIME_DEFINE_LINE_CACHE
    _DEFAULT_USING_PREFIX_CACHE = None
    _DEFAULT_DEFINE_CACHE = None
    _RUNTIME_DEFINE_LINE_CACHE = None


def current_mode_name():
    return _state.current_mode_name()


def current_server_base_url():
    return _state.current_server_base_url()


def current_execute_base_url():
    return _state.current_execute_base_url()
