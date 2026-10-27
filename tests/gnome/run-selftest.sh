#!/bin/sh
set -eu
export DISPLAY=:99
export XDG_RUNTIME_DIR=/tmp/typlo-gnome-runtime
mkdir -p "$XDG_RUNTIME_DIR"
chmod 700 "$XDG_RUNTIME_DIR"
# Shell needs a system-bus connection even without logind/colord in a disposable container.
# Use a separate unprivileged bus; no host services are exposed to this test.
export DBUS_SYSTEM_BUS_ADDRESS="unix:path=$XDG_RUNTIME_DIR/system-bus"
dbus-daemon --session --fork --address="$DBUS_SYSTEM_BUS_ADDRESS"
export NO_AT_BRIDGE=1
Xvfb :99 -screen 0 1280x800x24 >/tmp/xvfb.log 2>&1 &
xvfb_pid=$!
shell_pid=
trap 'kill "$xvfb_pid" ${shell_pid:-} 2>/dev/null || true' EXIT
sleep 1
gsettings set org.gnome.desktop.input-sources sources "[('xkb', 'us'), ('xkb', 'ru'), ('xkb', 'ua'), ('xkb', 'fr+oss'), ('xkb', 'de')]"
gsettings set org.gnome.desktop.input-sources mru-sources "[('xkb', 'us'), ('xkb', 'ru'), ('xkb', 'ua'), ('xkb', 'fr+oss'), ('xkb', 'de')]"
gsettings set org.gnome.desktop.interface enable-animations false
gsettings set org.gnome.shell enabled-extensions "['typlo-input-sources@bitcodepro']"
gnome-shell --x11 --replace --sm-disable >/tmp/gnome-shell.log 2>&1 &
shell_pid=$!
call() {
    gdbus call --session --dest org.gnome.Shell \
        --object-path /io/github/bitcodepro/Typlo/InputSources \
        --method "io.github.bitcodepro.Typlo.InputSources.$@"
}
ready=false
for attempt in $(seq 1 30); do
    if call Ping 2>/dev/null | grep -qx '(true,)'; then ready=true; break; fi
    sleep 1
done
if [ "$ready" != true ]; then cat /tmp/gnome-shell.log; exit 1; fi
test "$(call GetCurrent)" = "('us',)"
gsettings get org.gnome.desktop.input-sources sources | grep -q "'de'"
# GNOME starts with the first three sources plus a locale layout, not all five.
if setxkbmap -query | grep '^layout:' | grep -q 'de'; then
    printf '%s\n' 'Expected German to be outside the initial XKB group'; exit 1
fi
for source in de fr+oss us de us; do
    test "$(call Activate "$source")" = '(true,)'
    test "$(call GetCurrent)" = "('$source',)"
done
test "$(call Activate unknown)" = '(false,)'
test "$(call GetCurrent)" = "('us',)"
# Invoke Shell directly; the separate org.gnome.Shell.Extensions CLI service is not installed here.
gdbus call --session --dest org.gnome.Shell --object-path /org/gnome/Shell \
    --method org.gnome.Shell.Extensions.DisableExtension typlo-input-sources@bitcodepro | grep -qx '(true,)'
for attempt in $(seq 1 20); do
    if ! call Ping >/dev/null 2>&1; then break; fi
    sleep 0.2
done
if call Ping >/dev/null 2>&1; then
    cat /tmp/gnome-shell.log
    printf '%s\n' 'Bridge remained exported after disable'; exit 1
fi
printf '%s\n' 'PASS GNOME 46/X11: fifth source, variants, return to previous group, unavailable source, disable'
