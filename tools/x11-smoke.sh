#!/bin/sh
set -eu
test "$#" -eq 1
bin=$1
dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
state=$(mktemp -d)
server=""
resident=""
editor=""
cleanup() {
    for pid in "$resident" "$editor" "$server"; do
        if [ -n "$pid" ]; then kill "$pid" 2>/dev/null || true; fi
    done
}
trap cleanup EXIT
unset WAYLAND_DISPLAY
Xvfb -displayfd 3 -noreset -screen 0 900x600x24 3>"$state/display" >"$state/xvfb.log" 2>&1 &
server=$!
for _ in $(seq 1 100); do
    if [ -s "$state/display" ]; then break; fi
    sleep 0.05
done
export DISPLAY=:$(cat "$state/display")
export GTK_A11Y=1
setxkbmap -layout us,ru,ua
mkdir "$state/profile"
cat >"$state/profile/settings.json" <<'JSON'
{"cycle":["en","ru","uk"],"applications":[{"process":"TyploX11Restricted","cycle":["en","uk"]}],"snippets":[{"trigger":"qqy","text":"A{cursor}B"},{"trigger":"qqm","text":"line one\n{cursor}line two"}]}
JSON
python3 "$dir/x11_fixture.py" "$state" &
editor=$!
for _ in $(seq 1 100); do
    if [ -e "$state/ready" ]; then break; fi
    sleep 0.05
done
test -e "$state/ready"
"$bin" --managed-resident "$state/profile" "$state/resident.ready" >"$state/resident.log" 2>&1 &
resident=$!
expect() {
    if [ "$1" != "$2" ]; then
        printf 'Expected <%s>, received <%s>\n' "$2" "$1" >&2
        cat "$state/resident.log" >&2
        if [ -f "$state/accessibility" ]; then cat "$state/accessibility" >&2; fi
        exit 1
    fi
}
action() {
    printf '%s' "$1" >"$state/request.next"
    mv "$state/request.next" "$state/request"
    for _ in $(seq 1 50); do
        if [ -e "$state/ack" ] && [ "$(cat "$state/ack")" = "$1" ]; then break; fi
        sleep 0.02
    done
    expect "$(cat "$state/ack")" "$1"
    sleep 0.12
}
layout() { "$bin" --current-layout; }
cycle() {
    xdotool keydown Control_L keydown Shift_L keyup Shift_L keyup Control_L
    sleep 0.14
}
for _ in $(seq 1 60); do
    if [ -e "$state/resident.ready" ]; then break; fi
    sleep 0.05
done
expect "$(cat "$state/resident.ready")" "ready"
kill -0 "$resident"
expect "$("$bin" --list-layouts | wc -l | tr -d ' ')" 3
expect "$(layout)" "English (US)"
cycle
expect "$(layout)" "Russian"
printf 'PASS unrestricted layout cycle\n'
action restricted
expect "$(layout)" "English (US)"
cycle
expect "$(layout)" "Ukrainian"
cycle
expect "$(layout)" "English (US)"
xdotool key ctrl+shift+x
sleep 0.12
expect "$(layout)" "English (US)"
action clipboard
printf 'PASS restricted layout cycle and longer modifier chord\n'
action select
expect "$(xclip -selection primary -out)" ghbdsn
xdotool key ctrl+alt+shift+space
sleep 0.3
expect "$(cat "$state/value")" "🙂 prefix привіт suffix"
expect "$(xclip -selection clipboard -out)" "clipboard sentinel"
python3 - "$state/profile/history.sqlite3" <<'PY'
import sqlite3
import sys
with sqlite3.connect(sys.argv[1]) as db:
    rows = db.execute("select payload from clips where kind=1").fetchall()
    assert len(rows) == 1
    assert b"clipboard sentinel" not in rows[0][0]
PY
expect "$(layout)" "Ukrainian"
xdotool key ctrl+alt+shift+z
sleep 0.2
expect "$(cat "$state/value")" "🙂 prefix ghbdsn suffix"
printf 'PASS direct selection replacement, Unicode offsets, Undo and clipboard preservation\n'
action free
expect "$(layout)" "Ukrainian"
cycle
expect "$(layout)" "English (US)"
action clear
xdotool key --delay 4 g h b d s n space
sleep 0.35
expect "$(cat "$state/value")" "привіт "
expect "$(layout)" "Ukrainian"
xdotool key ctrl+alt+shift+z
sleep 0.2
expect "$(cat "$state/value")" "ghbdsn "
"$bin" --switch-layout en
action clear
xdotool key --delay 4 q q y
sleep 0.2
expect "$(cat "$state/value")" "AB"
xdotool key x
sleep 0.15
expect "$(cat "$state/value")" "AxB"
printf 'PASS dictionary autocorrection after space, Undo and snippet caret\n'
action spelling
xdotool key ctrl+shift+l
for _ in $(seq 1 100); do
    if [ "$(cat "$state/value")" = "🙂 Spelling tail" ]; then break; fi
    sleep 0.05
done
expect "$(cat "$state/value")" "🙂 Spelling tail"
printf 'PASS silent spell correction in the original selected range\n'
action multi-select
xdotool key ctrl+alt+shift+space
sleep 0.3
expect "$(cat "$state/multiline")" "$(printf '🙂 привіт tail\nsecond')"
expect "$(cat "$state/styled")" "True"
"$bin" --switch-layout en
action multi-snippet
xdotool key --delay 4 q q m
sleep 0.22
xdotool key x
sleep 0.15
expect "$(cat "$state/multiline")" "$(printf 'begin\nline one\nxline two')"
printf 'PASS multiline replacement, preserved formatting and multiline snippet caret\n'
if [ -x "$(dirname "$bin")/typlo-settings" ]; then
    "$bin" --switch-layout en
    action select
    original_focus=$(xdotool getwindowfocus)
    choice_window() {
        xdotool search --onlyvisible --name '^(Layout choices|Варианты раскладки)$' 2>/dev/null || true
    }
    await_choice() {
        for _ in $(seq 1 100); do
            choice=$(choice_window)
            if [ -n "$choice" ]; then return 0; fi
            sleep 0.05
        done
        expect "$choice" "visible choice preview"
    }
    await_closed_choice() {
        for _ in $(seq 1 60); do
            if [ -z "$(choice_window)" ]; then return 0; fi
            sleep 0.05
        done
        expect "$(choice_window)" ""
    }
    xdotool key ctrl+alt+shift+l
    await_choice
    first_choice=$choice
    expect "$(xdotool getwindowfocus)" "$original_focus"
    xdotool key ctrl+alt+shift+l
    sleep 0.14
    expect "$(choice_window)" "$first_choice"
    xdotool key ctrl+alt+shift+Escape
    await_closed_choice
    xdotool key ctrl+alt+shift+Return
    sleep 0.16
    expect "$(cat "$state/value")" "🙂 prefix ghbdsn suffix"
    xdotool key ctrl+alt+shift+l
    await_choice
    xdotool key ctrl+alt+shift+Return
    await_closed_choice
    expect "$(cat "$state/value")" "🙂 prefix привіт suffix"
    xdotool key ctrl+alt+shift+z
    sleep 0.25
    expect "$(cat "$state/value")" "🙂 prefix ghbdsn suffix"
    "$bin" --switch-layout en
    action select
    xdotool key ctrl+alt+shift+l
    await_choice
    action free
    await_closed_choice
    xdotool key ctrl+alt+shift+Return
    sleep 0.16
    expect "$(cat "$state/value")" "🙂 prefix ghbdsn suffix"
    action restricted
    printf 'PASS global dictionary choice cycle, cancel, apply, Undo and focus change\n'

    action clipboard-latest
    action clear
    xdotool key ctrl+shift+v
    history_window() {
        for window in $(xdotool search --onlyvisible --name . 2>/dev/null || true); do
            case $(xdotool getwindowname "$window" 2>/dev/null || true) in
                *'История буфера'*|*'история буфера'*|*'clipboard history'*)
                    printf '%s\n' "$window"
                    return 0
                    ;;
            esac
        done
    }
    for _ in $(seq 1 100); do
        history=$(history_window)
        if [ -n "$history" ]; then break; fi
        sleep 0.05
    done
    test -n "$history" || {
        cat "$state/resident.log" >&2
        xdotool search --onlyvisible --name . getwindowname %@ >&2 || true
        exit 1
    }
    xdotool windowfocus --sync "$history"
    xdotool key ctrl+shift+v
    for _ in $(seq 1 60); do
        if [ "$(cat "$state/value")" = "clipboard sentinel" ]; then break; fi
        sleep 0.05
    done
    expect "$(cat "$state/value")" "clipboard sentinel"
    expect "$(xclip -selection clipboard -out)" "clipboard sentinel"
    xdotool key ctrl+shift+v
    for _ in $(seq 1 60); do
        if [ "$(history_window)" = "$history" ]; then break; fi
        sleep 0.05
    done
    expect "$(history_window)" "$history"
    printf 'PASS previous history item pasted in the original GTK field and the popup reopened\n'
fi
cp "$state/profile/resident.lock" "$state/profile/stop.next"
mv "$state/profile/stop.next" "$state/profile/resident.stop"
for _ in $(seq 1 60); do
    if ! kill -0 "$resident" 2>/dev/null; then break; fi
    sleep 0.05
done
if kill -0 "$resident" 2>/dev/null; then
    cat "$state/resident.log" >&2
    exit 1
fi
wait "$resident"
resident=""
printf 'PASS managed resident readiness and graceful shutdown\n'
printf 'PASS X11 layout discovery, Ctrl+Shift, per-app layouts, verified selection replacement, Undo and clipboard preservation\n'
