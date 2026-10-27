# Real GNOME 46/X11 integration, independent of the host's desktop/settings.
FROM ubuntu:24.04
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      gnome-shell dbus-x11 xvfb x11-xkb-utils libgl1-mesa-dri libegl-mesa0 libglx-mesa0 \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --uid 1001 typlotest
COPY integrations/gnome/ /home/typlotest/.local/share/gnome-shell/extensions/typlo-input-sources@bitcodepro/
COPY tests/gnome/run-selftest.sh /run-selftest.sh
RUN chown -R typlotest:typlotest /home/typlotest && sed -i 's/\r$//' /run-selftest.sh
USER typlotest
ENV XDG_CURRENT_DESKTOP=GNOME XDG_SESSION_TYPE=x11 GNOME_SHELL_SESSION_MODE=user
ENTRYPOINT ["dbus-run-session", "--", "sh", "/run-selftest.sh"]
