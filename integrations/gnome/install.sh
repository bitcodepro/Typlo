#!/bin/sh
# Run explicitly as the desktop user; no root privileges or Shell unsafe mode are needed.
set -eu
source_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
target_dir="${XDG_DATA_HOME:-$HOME/.local/share}/gnome-shell/extensions/typlo-input-sources@bitcodepro"
mkdir -p "$target_dir"
for file in extension.js bridge.js metadata.json; do
    cp "$source_dir/$file" "$target_dir/$file"
done
printf '%s\n' 'Installed Typlo Input Sources for GNOME 45/46.'
if gnome-extensions enable typlo-input-sources@bitcodepro; then
    printf '%s\n' 'Enabled. Reopen Typlo settings to refresh platform capabilities.'
else
    printf '%s\n' 'Log out and back in, then run: gnome-extensions enable typlo-input-sources@bitcodepro'
fi
