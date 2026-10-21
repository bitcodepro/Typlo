# End-to-end self-test of the Linux build on a virtual X11 display.
FROM ubuntu:24.04
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      xvfb openbox xclip xdotool dbus-x11 libice6 libsm6 libfontconfig1 libx11-6 libxtst6 libxt6 \
      libxi6 x11-xserver-utils libxrandr2 libxcursor1 libxinerama1 libxkbcommon-x11-0 libx11-xcb1 libicu74 ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY run-selftest.sh /run-selftest.sh
RUN sed -i 's/\r$//' /run-selftest.sh && chmod +x /run-selftest.sh
ENTRYPOINT ["/run-selftest.sh"]
