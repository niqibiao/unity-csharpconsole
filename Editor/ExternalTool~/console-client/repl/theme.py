import os
from functools import lru_cache

from prompt_toolkit.styles import Style, merge_styles, style_from_pygments_cls

try:
    from pygments.styles import get_all_styles, get_style_by_name
except Exception:
    get_all_styles = None
    get_style_by_name = None

DEFAULT_THEME = "material"
_THEME_FILE_NAME = "theme.txt"


@lru_cache(maxsize=1)
def list_themes():
    if get_all_styles is None:
        return ()
    return tuple(sorted(get_all_styles()))


class ThemeManager:
    """Owns the active pygments theme: commit/persist on /theme, transient preview while selecting."""

    def __init__(self, ui_style, cache_dir=None, default_theme=DEFAULT_THEME):
        self._ui_style = ui_style if ui_style is not None else Style.from_dict({})
        self._theme_file = os.path.join(cache_dir, _THEME_FILE_NAME) if cache_dir else None
        self._style_cache = {}
        self._preview_theme = None
        self._committed_theme = self._load_persisted_theme() or default_theme
        if not self.is_valid(self._committed_theme):
            self._committed_theme = default_theme

    def _load_persisted_theme(self):
        if self._theme_file is None or not os.path.isfile(self._theme_file):
            return None
        try:
            with open(self._theme_file, "r", encoding="utf-8") as f:
                name = f.read().strip()
        except Exception:
            return None
        return name if self.is_valid(name) else None

    def _persist_theme(self, name):
        if self._theme_file is None:
            return
        try:
            with open(self._theme_file, "w", encoding="utf-8") as f:
                f.write(name + "\n")
        except Exception:
            pass

    @staticmethod
    def is_valid(name):
        return bool(name) and name in list_themes()

    def current_theme(self):
        return self._committed_theme

    def active_theme(self):
        return self._preview_theme or self._committed_theme

    def active_style(self):
        name = self.active_theme()
        if get_style_by_name is None:
            return self._ui_style
        cached = self._style_cache.get(name)
        if cached is None:
            cached = merge_styles([style_from_pygments_cls(get_style_by_name(name)), self._ui_style])
            self._style_cache[name] = cached
        return cached

    def set_theme(self, name):
        if not self.is_valid(name):
            return False
        self._committed_theme = name
        self._preview_theme = None
        self._persist_theme(name)
        return True

    def preview(self, name):
        """Previews a candidate theme; invalid names revert to the committed theme. Returns True when the active style changed."""
        target = name if self.is_valid(name) else None
        if target == self._preview_theme:
            return False
        self._preview_theme = target
        return True

    def clear_preview(self):
        if self._preview_theme is None:
            return False
        self._preview_theme = None
        return True
