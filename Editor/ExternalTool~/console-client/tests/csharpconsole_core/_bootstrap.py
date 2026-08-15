import os
import sys

TESTS_DIR = os.path.dirname(os.path.abspath(__file__))
CORE_TESTS_ROOT = os.path.dirname(TESTS_DIR)
CONSOLE_CLIENT_ROOT = os.path.dirname(CORE_TESTS_ROOT)
if CONSOLE_CLIENT_ROOT not in sys.path:
    sys.path.insert(0, CONSOLE_CLIENT_ROOT)
