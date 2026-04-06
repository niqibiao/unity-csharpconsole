import os


class SharedConfigState:
    def __init__(self):
        self.ip = ""
        self.port = 0
        self.runtime_mode = False
        self.compile_ip = ""
        self.compile_port = 0
        self.runtime_ip = ""
        self.runtime_port = 0
        self.runtime_dll_path = ""
        self.parsed_args = None

    def current_mode_name(self):
        return "runtime" if self.runtime_mode else "editor"

    def current_server_base_url(self):
        if self.runtime_mode:
            return f"http://{self.compile_ip}:{self.compile_port}/CSharpConsole"
        return f"http://{self.ip}:{self.port}/CSharpConsole"

    def current_execute_base_url(self):
        if self.runtime_mode:
            return f"http://{self.runtime_ip}:{self.runtime_port}/CSharpConsole"
        return f"http://{self.ip}:{self.port}/CSharpConsole"


def make_cache_paths(script_dir):
    cache_csharp_dir = os.path.join(script_dir, ".cache", "csharp")
    log_file_name = "csharp_console_history.txt"
    log_file_path = os.path.join(cache_csharp_dir, log_file_name)
    default_using_path = os.path.join(cache_csharp_dir, "DefaultUsing.cs")
    default_define_path = os.path.join(cache_csharp_dir, "Defines.txt")
    os.makedirs(cache_csharp_dir, exist_ok=True)
    return {
        "_cache_csharp_dir": cache_csharp_dir,
        "_log_file_name": log_file_name,
        "_log_file_path": log_file_path,
        "_default_using_path": default_using_path,
        "_default_define_path": default_define_path,
    }


def add_common_connection_args(parser, extra_arg_adder):
    parser.add_argument('--ip', required=True)
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--editor', action='store_true', help='target is Editor (compatibility alias for --mode editor)')
    parser.add_argument('--mode', choices=['editor', 'runtime'])
    parser.add_argument('--runtime-dll-path', default='')
    parser.add_argument('--compile-ip', default='127.0.0.1')
    parser.add_argument('--compile-port', type=int, default=14500)
    extra_arg_adder(parser)


def configure_shared_globals(state, args, extra_configurator=None):
    state.parsed_args = args
    selected_mode = args.mode or ('editor' if args.editor else 'runtime')
    state.runtime_mode = selected_mode == 'runtime'
    state.ip = args.ip
    state.port = args.port
    state.runtime_dll_path = args.runtime_dll_path
    state.compile_ip = args.compile_ip
    state.compile_port = args.compile_port

    if extra_configurator is not None:
        extra_configurator(args)

    if state.runtime_mode:
        state.runtime_ip = state.ip
        state.runtime_port = state.port
    else:
        state.runtime_ip = ''
        state.runtime_port = 0
