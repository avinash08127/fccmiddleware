#!/usr/bin/env bash
# Installs or uninstalls the FCC Desktop Agent as a systemd service.
#
# Usage:
#   sudo ./install-linux.sh [install|uninstall] [/path/to/FccDesktopAgent.Service]
#
# Defaults:
#   action  = install
#   exe     = <script directory>/FccDesktopAgent.Service

set -euo pipefail

ACTION="${1:-install}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXE_PATH="${2:-$SCRIPT_DIR/FccDesktopAgent.Service}"

SERVICE_NAME="fcc-desktop-agent"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
SERVICE_USER="fccagent"

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

  # Create a dedicated system user if it does not exist
  if ! id -u "$SERVICE_USER" &>/dev/null; then
    useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
    echo "Created system user '$SERVICE_USER'."
  fi

  # Ensure log directory exists and is owned by the service user
  LOG_DIR="/var/log/fcc-desktop-agent"
  mkdir -p "$LOG_DIR"
  chown "$SERVICE_USER:$SERVICE_USER" "$LOG_DIR"

  echo "Writing $SERVICE_FILE ..."
  cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=FCC Desktop Agent
Documentation=https://github.com/your-org/fccmiddleware
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User=${SERVICE_USER}
ExecStart=${EXE_PATH}
Restart=on-failure
RestartSec=10
TimeoutStopSec=30

# Hardening
NoNewPrivileges=true
ProtectSystem=full
PrivateTmp=true

[Install]
WantedBy=multi-user.target
EOF

  systemctl daemon-reload
  systemctl enable "$SERVICE_NAME"
  systemctl start  "$SERVICE_NAME"

  echo "Service '$SERVICE_NAME' installed and started."
  systemctl status "$SERVICE_NAME" --no-pager || true
}

uninstall_service() {
  if systemctl is-active --quiet "$SERVICE_NAME"; then
    echo "Stopping $SERVICE_NAME ..."
    systemctl stop "$SERVICE_NAME"
  fi

  if systemctl is-enabled --quiet "$SERVICE_NAME" 2>/dev/null; then
    systemctl disable "$SERVICE_NAME"
  fi

  if [[ -f "$SERVICE_FILE" ]]; then
    rm -f "$SERVICE_FILE"
    systemctl daemon-reload
    echo "Removed $SERVICE_FILE."
  else
    echo "Unit file not found — nothing to remove."
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
