#!/bin/sh
# Expects the linux-x64 publish output mounted at /app and writes /out/selftest.txt.
export DISPLAY=:99
Xvfb :99 -screen 0 1280x800x24 >/dev/null 2>&1 &
sleep 1
openbox >/dev/null 2>&1 &
sleep 1
chmod +x /app/Typlo
timeout 180 /app/Typlo --selftest /out/selftest.txt
code=$?
cat /out/selftest.txt 2>/dev/null
for f in /app/data/typlo.log /root/.local/share/Typlo/typlo.log; do [ -f "$f" ] && tail -40 "$f"; done
exit $code
