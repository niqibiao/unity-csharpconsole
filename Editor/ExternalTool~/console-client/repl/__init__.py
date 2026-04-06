import os
import sys


_CONSOLE_CLIENT_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _CONSOLE_CLIENT_ROOT not in sys.path:
    sys.path.insert(0, _CONSOLE_CLIENT_ROOT)
