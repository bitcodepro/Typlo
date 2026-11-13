import sys
from pathlib import Path

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
gi.require_version("Atk", "1.0")
from gi.repository import Atk, Gdk, GLib, Gtk

state = Path(sys.argv[1])
root = Gtk.Window(title="Typlo X11 unrestricted")
root.set_wmclass("typlo-free", "TyploX11Free")
restricted = Gtk.Window(title="Typlo X11 restricted")
restricted.set_wmclass("typlo-restricted", "TyploX11Restricted")
entry = Gtk.Entry()
multiline = Gtk.TextView()
buffer = multiline.get_buffer()
tail_style = buffer.create_tag("tail", weight=700)
box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
box.pack_start(entry, True, True, 0)
box.pack_start(multiline, True, True, 0)
restricted.add(box)

def report(*_):
    (state / "value").write_text(entry.get_text(), encoding="utf-8")

def report_multiline(*_):
    start, end = buffer.get_bounds()
    text = buffer.get_text(start, end, True)
    (state / "multiline").write_text(text, encoding="utf-8")
    offset = text.find("tail")
    styled = offset >= 0 and buffer.get_iter_at_offset(offset).has_tag(tail_style)
    (state / "styled").write_text(str(styled), encoding="utf-8")

def poll():
    request = state / "request"
    if request.exists():
        action = request.read_text(encoding="utf-8")
        request.unlink()
        if action == "free":
            root.get_window().focus(Gdk.CURRENT_TIME)
        elif action == "restricted":
            restricted.get_window().focus(Gdk.CURRENT_TIME)
            entry.grab_focus()
        elif action == "select":
            restricted.get_window().focus(Gdk.CURRENT_TIME)
            entry.grab_focus()
            entry.set_text("🙂 prefix ghbdsn suffix")
            entry.select_region(9, 15)
            states = entry.get_accessible().ref_state_set()
            flags = [state.value_nick for state in (
                Atk.StateType.FOCUSED, Atk.StateType.SENSITIVE,
                Atk.StateType.SINGLE_LINE, Atk.StateType.EDITABLE,
            ) if states.contains_state(state)]
            (state / "accessibility").write_text(
                f"{entry.get_accessible().get_role().value_nick}: {flags}", encoding="utf-8"
            )
        elif action == "clear":
            restricted.get_window().focus(Gdk.CURRENT_TIME)
            entry.grab_focus()
            entry.set_text("")
        elif action == "clipboard":
            Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD).set_text("clipboard sentinel", -1)
        elif action == "clipboard-latest":
            Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD).set_text("latest sentinel", -1)
        elif action == "spelling":
            restricted.get_window().focus(Gdk.CURRENT_TIME)
            entry.grab_focus()
            entry.set_text("🙂 Speling tail")
            entry.select_region(2, 9)
        elif action == "multi-select":
            restricted.get_window().focus(Gdk.CURRENT_TIME)
            multiline.grab_focus()
            buffer.set_text("🙂 ghbdsn tail\nsecond")
            buffer.apply_tag(tail_style, buffer.get_iter_at_offset(9), buffer.get_iter_at_offset(13))
            buffer.select_range(buffer.get_iter_at_offset(2), buffer.get_iter_at_offset(8))
            report_multiline()
        elif action == "multi-snippet":
            restricted.get_window().focus(Gdk.CURRENT_TIME)
            multiline.grab_focus()
            buffer.set_text("begin\n")
            buffer.place_cursor(buffer.get_end_iter())
        (state / "ack").write_text(action, encoding="utf-8")
    return True

entry.connect("changed", report)
buffer.connect("changed", report_multiline)
root.show_all()
restricted.show_all()
root.get_window().focus(Gdk.CURRENT_TIME)
(state / "ready").touch()
GLib.timeout_add(10, poll)
Gtk.main()
