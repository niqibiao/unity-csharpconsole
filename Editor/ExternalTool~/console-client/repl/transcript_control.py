import time

from prompt_toolkit.data_structures import Point
from prompt_toolkit.document import Document
from prompt_toolkit.layout.controls import UIControl, UIContent
from prompt_toolkit.mouse_events import MouseButton, MouseEventType
from prompt_toolkit.selection import SelectionState, SelectionType
from prompt_toolkit.utils import get_cwidth

from . import session_ui


class TranscriptControl(UIControl):
    _EMPTY_LINE_PLACEHOLDER = [("", " ")]

    def __init__(self, transcript_state, code_fragment_renderer=None):
        self._transcript_state = transcript_state
        self._code_fragment_renderer = code_fragment_renderer
        self._last_line_count = 1
        self._follow_tail = True
        self._scroll_anchor_line = 0
        self._round_starts = [0]
        self._scroll_targets = [0]
        self._cursor_position = 0
        self.selection_state = None
        self._line_plain_texts = [""]
        self._line_start_indexes = [0]
        self._plain_text = ""
        self._mouse_selecting = False
        self._last_click_timestamp = None

    def is_focusable(self):
        return False

    @property
    def document(self):
        return Document(
            text=self._plain_text,
            cursor_position=max(0, min(len(self._plain_text), self._cursor_position)),
            selection=self.selection_state,
        )

    def copy_selection(self):
        _, clipboard_data = self.document.cut_selection()
        self.selection_state = None
        return clipboard_data

    def mouse_handler(self, mouse_event):
        if mouse_event.event_type == MouseEventType.SCROLL_UP:
            self.move_cursor_up()
            return None
        if mouse_event.event_type == MouseEventType.SCROLL_DOWN:
            self.move_cursor_down()
            return None
        if mouse_event.button not in (MouseButton.LEFT, MouseButton.NONE):
            return NotImplemented

        line_index = self._clamp_line_index(mouse_event.position.y)
        include_line_end = mouse_event.event_type in (MouseEventType.MOUSE_MOVE, MouseEventType.MOUSE_UP)
        index = self._translate_mouse_position_to_index(
            mouse_event.position.x,
            line_index,
            include_line_end=include_line_end,
        )

        if mouse_event.event_type == MouseEventType.MOUSE_DOWN:
            self.selection_state = None
            self._cursor_position = index
            self._mouse_selecting = mouse_event.button == MouseButton.LEFT
            return None

        if mouse_event.event_type == MouseEventType.MOUSE_MOVE and self._mouse_selecting:
            if self.selection_state is None and abs(self._cursor_position - index) > 0:
                self.selection_state = SelectionState(self._cursor_position, SelectionType.CHARACTERS)
            self._cursor_position = index
            return None

        if mouse_event.event_type == MouseEventType.MOUSE_UP and self._mouse_selecting:
            if abs(self._cursor_position - index) > 0:
                if self.selection_state is None:
                    self.selection_state = SelectionState(self._cursor_position, SelectionType.CHARACTERS)
                self._cursor_position = index
            else:
                double_click = (
                    self._last_click_timestamp is not None
                    and time.time() - self._last_click_timestamp < 0.3
                )
                self._cursor_position = index
                if double_click and self._plain_text:
                    start, end = self.document.find_boundaries_of_current_word()
                    self._cursor_position += start
                    self.selection_state = SelectionState(self._cursor_position, SelectionType.CHARACTERS)
                    self._cursor_position += end - start
            self._mouse_selecting = False
            self._last_click_timestamp = time.time()
            return None
        return NotImplemented

    def move_cursor_down(self, count=1):
        self._follow_tail = False
        next_targets = [line for line in self._scroll_targets if line > self._scroll_anchor_line]
        if next_targets:
            self._scroll_anchor_line = next_targets[0]
            return
        self._scroll_anchor_line = min(max(0, self._last_line_count - 1), self._scroll_anchor_line + max(1, int(count)))

    def move_cursor_up(self, count=1):
        self._follow_tail = False
        previous_targets = [line for line in self._scroll_targets if line < self._scroll_anchor_line]
        if previous_targets:
            self._scroll_anchor_line = previous_targets[-1]
            return
        self._scroll_anchor_line = max(0, self._scroll_anchor_line - max(1, int(count)))

    def follow_tail(self):
        self._follow_tail = True

    def get_vertical_scroll(self):
        return self._scroll_anchor_line

    def _entry_starts_new_round(self, current_entry, previous_entry):
        if previous_entry is None:
            return False
        return previous_entry.entry_type == "result" and current_entry.entry_type == "input"

    def _entry_ends_round(self, entry):
        return entry is not None and entry.entry_type == "result"

    def _render_transcript_code_fragments(self, text):
        if callable(self._code_fragment_renderer):
            return self._code_fragment_renderer(text)
        return [("class:transcript.input.text", text or "")]

    def _normalize_entry_text(self, text):
        return (text or "").rstrip("\n")

    def _build_entry_prefix_and_body(self, entry):
        timestamp = [("class:transcript.timestamp", f"[{session_ui.format_transcript_timestamp(entry.created_at)}] ")]
        if entry.entry_type == "input":
            return (
                [*timestamp, ("class:transcript.input.prefix", "> ")],
                self._render_transcript_code_fragments(self._normalize_entry_text(entry.text)),
            )
        if entry.entry_type == "result" and entry.ok:
            return (
                [*timestamp, ("class:transcript.result.prefix", "< ")],
                [("class:transcript.result.text", self._normalize_entry_text(entry.text))],
            )
        if entry.entry_type == "result":
            return (
                [*timestamp, (f"class:transcript.error.{entry.error_kind or 'transport_error'}.prefix", "! ")],
                [(f"class:transcript.error.{entry.error_kind or 'transport_error'}.text", self._normalize_entry_text(entry.text or entry.summary))],
            )
        return (
            [*timestamp, ("class:transcript.info.prefix", "· ")],
            [("class:transcript.info.text", self._normalize_entry_text(entry.text))],
        )

    def _render_entry_fragments(self, entry):
        prefix_fragments, content_fragments = self._build_entry_prefix_and_body(entry)
        return [*prefix_fragments, *content_fragments]

    def _build_continuation_prefix(self, prefix_fragments):
        return [("", " " * self._fragments_display_width(prefix_fragments))]

    def _fragments_display_width(self, fragments):
        total = 0
        for _style, text, *_rest in fragments:
            for char in text:
                total += max(0, get_cwidth(char))
        return total

    def _split_fragments_by_newline(self, fragments):
        lines = [[]]
        for style, text, *rest in fragments:
            parts = text.split("\n")
            for index, part in enumerate(parts):
                if part:
                    lines[-1].append((style, part, *rest))
                if index < len(parts) - 1:
                    lines.append([])
        return lines

    def _append_wrapped_line(self, target_lines, prefix_fragments, continuation_fragments, content_fragments, width):
        if width is None or width <= 0:
            target_lines.append([*prefix_fragments, *content_fragments])
            return

        if not content_fragments:
            target_lines.append(list(prefix_fragments))
            return

        current_prefix = list(prefix_fragments)
        continuation_prefix = list(continuation_fragments)
        current_prefix_width = self._fragments_display_width(current_prefix)
        continuation_width = self._fragments_display_width(continuation_prefix)
        current_line = list(current_prefix)
        current_width = current_prefix_width
        current_line_has_content = False

        def flush_current_line():
            nonlocal current_line, current_width, current_line_has_content, current_prefix_width
            target_lines.append(current_line)
            current_line = list(continuation_prefix)
            current_width = continuation_width
            current_prefix_width = continuation_width
            current_line_has_content = False

        for fragment in content_fragments:
            style, text, *rest = fragment
            if not text:
                current_line.append(fragment)
                continue

            text_buffer = []
            text_buffer_width = 0

            def flush_text_buffer():
                nonlocal text_buffer, text_buffer_width, current_width, current_line_has_content
                if text_buffer:
                    current_line.append((style, "".join(text_buffer), *rest))
                    current_width += text_buffer_width
                    current_line_has_content = True
                    text_buffer = []
                    text_buffer_width = 0

            for char in text:
                char_width = max(0, get_cwidth(char))
                if current_width + text_buffer_width + char_width > width and (
                    current_width > current_prefix_width or text_buffer_width > 0
                ):
                    flush_text_buffer()
                    flush_current_line()

                text_buffer.append(char)
                text_buffer_width += char_width

                if current_width + text_buffer_width >= width and current_width > current_prefix_width:
                    flush_text_buffer()
                    flush_current_line()

            flush_text_buffer()

        if current_line_has_content or not target_lines:
            target_lines.append(current_line)

    def _append_entry_lines(self, target_lines, entry, width):
        prefix_fragments, content_fragments = self._build_entry_prefix_and_body(entry)
        continuation_fragments = self._build_continuation_prefix(prefix_fragments)
        logical_lines = self._split_fragments_by_newline(content_fragments)
        for index, logical_line in enumerate(logical_lines):
            self._append_wrapped_line(
                target_lines,
                prefix_fragments if index == 0 else continuation_fragments,
                continuation_fragments,
                logical_line,
                width,
            )

    def _style_selected_fragments(self, fragments, line_start_index):
        if self.selection_state is None:
            return fragments

        ranges = list(self.document.selection_ranges())
        if not ranges:
            return fragments

        result = []
        text_index = line_start_index

        for style, text, *rest in fragments:
            if not text:
                result.append((style, text, *rest))
                continue

            chunk = []
            chunk_selected = None
            for char in text:
                selected = any(start <= text_index < end for start, end in ranges)
                if chunk_selected is None:
                    chunk_selected = selected
                if chunk_selected != selected:
                    result.append((self._merge_selection_style(style, chunk_selected), "".join(chunk), *rest))
                    chunk = []
                    chunk_selected = selected
                chunk.append(char)
                text_index += 1

            if chunk:
                result.append((self._merge_selection_style(style, chunk_selected), "".join(chunk), *rest))

        return result

    def _merge_selection_style(self, style, selected):
        if not selected:
            return style
        return f"{style} reverse".strip()

    def _clamp_line_index(self, line_index):
        return max(0, min(len(self._line_plain_texts) - 1, int(line_index or 0)))

    def _translate_mouse_position_to_index(self, x, line_index, include_line_end=False):
        x = max(0, int(x or 0))
        line_text = self._line_plain_texts[self._clamp_line_index(line_index)]
        total_width = sum(max(1, get_cwidth(char)) for char in line_text)
        if include_line_end and line_text and x >= max(0, total_width - 1):
            return self._line_start_indexes[self._clamp_line_index(line_index)] + len(line_text)
        display_x = 0
        char_index = 0
        for char in line_text:
            char_width = max(1, get_cwidth(char))
            if display_x + char_width > x:
                break
            display_x += char_width
            char_index += 1
        return self._line_start_indexes[self._clamp_line_index(line_index)] + char_index

    def _get_display_line(self, lines, line_index):
        fragments = lines[line_index]
        if not fragments:
            return self._EMPTY_LINE_PLACEHOLDER
        return self._style_selected_fragments(fragments, self._line_start_indexes[line_index])

    def _append_round_separator_block(self, lines, width):
        lines.append([])
        lines.append(session_ui.render_transcript_round_separator(width))
        lines.append([])

    def _build_lines(self, width):
        lines = []
        round_starts = []
        previous_entry = None
        for entry in self._transcript_state.entries:
            if previous_entry is not None:
                lines.append([])
                if self._entry_starts_new_round(entry, previous_entry):
                    lines.append(session_ui.render_transcript_round_separator(width))
                    lines.append([])
            if entry.entry_type == "input":
                round_starts.append(len(lines))
            self._append_entry_lines(lines, entry, width)
            previous_entry = entry
        if round_starts and self._entry_ends_round(previous_entry):
            self._append_round_separator_block(lines, width)
        if not lines:
            lines = [[]]
        self._round_starts = round_starts or [0]
        return lines

    def create_content(self, width, height):
        lines = self._build_lines(width)
        self._line_plain_texts = ["".join(text for _style, text, *_rest in line) for line in lines]
        self._line_start_indexes = []
        running_index = 0
        for line_text in self._line_plain_texts:
            self._line_start_indexes.append(running_index)
            running_index += len(line_text) + 1
        self._plain_text = "\n".join(self._line_plain_texts)
        self._cursor_position = max(0, min(len(self._plain_text), self._cursor_position))
        if self.selection_state is not None and self.selection_state.original_cursor_position > len(self._plain_text):
            self.selection_state = None
        line_count = len(lines)
        visible_height = max(1, height)
        max_line_index = max(0, line_count - 1)
        max_scroll_anchor = max(0, line_count - visible_height)
        self._last_line_count = line_count
        self._scroll_targets = sorted(set([0, *self._round_starts, max_scroll_anchor]))
        if self._follow_tail:
            self._scroll_anchor_line = max_scroll_anchor
        else:
            self._scroll_anchor_line = min(max_scroll_anchor, self._scroll_anchor_line)
        cursor_line = min(max_line_index, self._scroll_anchor_line + visible_height - 1)
        return UIContent(
            get_line=lambda i: self._get_display_line(lines, i),
            line_count=line_count,
            cursor_position=Point(x=0, y=cursor_line),
            show_cursor=False,
        )
