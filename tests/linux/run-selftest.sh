#!/bin/sh
# Expects the linux-x64 publish output mounted at /app and writes /out/selftest.txt.
set -eu
# LocalApplicationData must resolve to an existing directory when /app is read-only.
# Otherwise .NET can return an empty base and "Typlo" collides with /app/Typlo.
session_dir=$(mktemp -d /tmp/typlo-x11.XXXXXX)
export XDG_DATA_HOME="$session_dir/data"
export XDG_CONFIG_HOME="$session_dir/config"
mkdir -p "$XDG_DATA_HOME" "$XDG_CONFIG_HOME"
export DISPLAY=:99
Xvfb :99 -screen 0 1280x800x24 >/dev/null 2>&1 &
xvfb_pid=$!
trap 'kill "$xvfb_pid" ${wm_pid:-} 2>/dev/null || true' EXIT
sleep 1
openbox >/dev/null 2>&1 &
wm_pid=$!
sleep 1
test -x /app/Typlo
mkdir -p /out
set +e
timeout 180 /app/Typlo --selftest /out/selftest.txt
code=$?
set -e
cat /out/selftest.txt 2>/dev/null || true
for f in /app/data/typlo.log "$XDG_DATA_HOME/Typlo/typlo.log" /root/.local/share/Typlo/typlo.log; do
    if [ -f "$f" ]; then tail -40 "$f"; fi
done
if [ "$code" -eq 0 ]; then grep -qx 'ALL PASSED' /out/selftest.txt || code=1; fi
exit $code
