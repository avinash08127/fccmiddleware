#!/usr/bin/env bash
# Installs or uninstalls the FCC Desktop Agent as a launchd daemon (macOS).
#
# Usage:
#   sudo ./install-macos.sh [install|uninstall] [/path/to/FccDesktopAgent.Service]
#
# Defaults:
#   action = install
#   exe    = <script directory>/FccDesktopAgent.Service

set -euo pipefail

ACTION="${1:-install}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXE_PATH="${2:-$SCRIPT_DIR/FccDesktopAgent.Service}"

LABEL="com.fccmiddleware.desktopagent"
PLIST_FILE="/Library/LaunchDaemons/${LABEL}.plist"
LOG_DIR="/var/log/fcc-desktop-agent"

if [[ $EUID -ne 0 ]]; then
  echo "Error: this script must be run as root (sudo)." >&2
  exit 1
fi

install_service() {
  if [[ ! -f "$EXE_PATH" ]]; then
    echo "Error: executable not found: $EXE_PATH" >&2
    exit 1
  fi

  chmod +x "$EXE_PATH"
  mkdir -p "$LOG_DIR"

  echo "Writing $PLIST_FILE ..."
  cat > "$PLIST_FILE" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>${LABEL}</string>

  <key>ProgramArguments</key>
  <array>
    <string>${EXE_PATH}</string>
  </array>

  <key>RunAtLoad</key>
  <true/>

  <key>KeepAlive</key>
  <true/>

  <key>StandardOutPath</key>
  <string>${LOG_DIR}/stdout.log</string>

  <key>StandardErrorPath</key>
  <string>${LOG_DIR}/stderr.log</string>

  <key>ThrottleInterval</key>
  <integer>10</integer>
</dict>
</plist>
EOF

  # Fix ownership — launchd daemons must be owned by root
  chown root:wheel "$PLIST_FILE"
  chmod 644 "$PLIST_FILE"

  launchctl load -w "$PLIST_FILE"
  echo "Daemon '$LABEL' loaded and started."
  launchctl list | grep "$LABEL" || true
}

uninstall_service() {
  if launchctl list | grep -q "$LABEL"; then
    echo "Unloading $LABEL ..."
    launchctl unload -w "$PLIST_FILE" 2>/dev/null || true
  fi

  if [[ -f "$PLIST_FILE" ]]; then
    rm -f "$PLIST_FILE"
    echo "Removed $PLIST_FILE."
  else
    echo "Plist not found — nothing to remove."
  fi

  echo "Uninstall complete."
}

case "$ACTION" in
  install)   install_service ;;
  uninstall) uninstall_service ;;
  *)
    echo "Usage: $0 [install|uninstall] [/path/to/exe]" >&2
    exit 1
    ;;
esac
