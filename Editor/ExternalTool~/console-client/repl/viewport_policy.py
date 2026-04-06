from prompt_toolkit.layout.dimension import Dimension
from prompt_toolkit.utils import get_cwidth


def _count_visual_lines(text, available_width):
    if available_width is None or available_width <= 0:
        return text.count("\n") + 1

    total = 0
    for logical_line in text.split("\n"):
        if not logical_line:
            total += 1
            continue
        wrapped = (get_cwidth(logical_line) + available_width - 1) // available_width
        total += max(1, wrapped)
    return total


def compute_input_height(document_text, available_width=None, max_visible_lines=8):
    text = document_text or ""
    visual_lines = _count_visual_lines(text, available_width)
    visible_lines = max(1, min(max_visible_lines, visual_lines))
    return Dimension.exact(visible_lines)


def compute_input_visible_lines(document_text, available_width=None, max_visible_lines=8):
    return compute_input_height(
        document_text,
        available_width=available_width,
        max_visible_lines=max_visible_lines,
    ).preferred


def is_transcript_at_bottom(window):
    info = getattr(window, "render_info", None)
    if info is None:
        return True
    return info.vertical_scroll >= max(0, info.content_height - info.window_height)


def pin_transcript_to_bottom(window):
    info = getattr(window, "render_info", None)
    if info is not None:
        window.vertical_scroll = max(0, info.content_height - info.window_height)
    else:
        window.vertical_scroll = max(0, getattr(window, "vertical_scroll", 0) + 10_000)


def maybe_preserve_transcript_tail(window, was_at_bottom):
    if was_at_bottom:
        pin_transcript_to_bottom(window)
        return True
    return False
