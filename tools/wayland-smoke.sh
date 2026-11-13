#!/bin/sh
set -eu
test "$#" -eq 1
bin=$1
state=$(mktemp -d)
export XDG_RUNTIME_DIR="$state/runtime"
export XDG_CONFIG_HOME="$state/config"
export WLR_BACKENDS=headless WLR_LIBINPUT_NO_DEVICES=1 WLR_RENDERER=pixman
export XDG_SESSION_TYPE=wayland
mkdir -m 700 "$XDG_RUNTIME_DIR"
mkdir "$XDG_CONFIG_HOME"
unset DISPLAY WAYLAND_DISPLAY
cat >"$state/sway.conf" <<'CONF'
output * resolution 800x600
seat seat0 fallback true
CONF
resident=""
sway -c "$state/sway.conf" >"$state/sway.log" 2>&1 &
compositor=$!
cleanup() {
    if [ -n "$resident" ]; then kill "$resident" 2>/dev/null || true; fi
    kill "$compositor" 2>/dev/null || true
}
trap cleanup EXIT
for _ in $(seq 1 100); do
    socket=$(find "$XDG_RUNTIME_DIR" -type s -name 'wayland-*' -print -quit)
    if [ -n "$socket" ]; then break; fi
    sleep 0.05
done
if [ -z "$socket" ]; then cat "$state/sway.log" >&2; exit 1; fi
export WAYLAND_DISPLAY=${socket##*/}
"$bin" >"$state/typlo.log" 2>&1 &
resident=$!
sleep 0.2
printf '%s' 'ghbdtn' | wl-copy --type 'text/plain;charset=utf-8'
db="$XDG_CONFIG_HOME/Typlo-Rust/history.sqlite3"
wait_for_history() {
    for _ in $(seq 1 60); do
        if [ -e "$db" ] && python3 - "$db" "$1" 2>/dev/null <<'PY'
import sqlite3
import sys
with sqlite3.connect(sys.argv[1]) as db:
    rows = db.execute("select payload from clips").fetchall()
    assert len(rows) >= int(sys.argv[2])
    assert all("привет".encode() not in row[0] and b"ghbdtn" not in row[0] for row in rows)
PY
        then return 0; fi
        sleep 0.05
    done
    cat "$state/typlo.log" >&2
    return 1
}
wait_for_history 1
"$bin" --convert ru
test "$(wl-paste --no-newline --type text)" = 'привет'
wait_for_history 2
printf 'PASS Wayland clipboard conversion and encrypted capture\n'
