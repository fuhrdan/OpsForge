#!/usr/bin/env bash
set -euo pipefail

SOURCE_DIR="${1:-./publish/linux-x64}"
INSTALL_DIR="${OPSFORGE_INSTALL_DIR:-/opt/opsforge-agent}"
DATA_DIR="${OPSFORGE_DATA_DIR:-/var/lib/opsforge}"
CONFIG_SOURCE="${OPSFORGE_CONFIG:-OpsForge.Agent/appsettings.linux-example.json}"

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run this installer with sudo/root." >&2
  exit 1
fi

if [[ ! -d "${SOURCE_DIR}" ]]; then
  echo "Published agent directory not found: ${SOURCE_DIR}" >&2
  echo "Run: dotnet publish OpsForge.Agent/OpsForge.Agent.csproj -c Release -r linux-x64 --self-contained false -o publish/linux-x64" >&2
  exit 1
fi

if ! id opsforge >/dev/null 2>&1; then
  useradd --system --home-dir "${DATA_DIR}" --shell /usr/sbin/nologin opsforge
fi

install -d -o root -g root -m 0755 "${INSTALL_DIR}"
install -d -o opsforge -g opsforge -m 0750 "${DATA_DIR}"

cp -a "${SOURCE_DIR}/." "${INSTALL_DIR}/"
chmod 0755 "${INSTALL_DIR}/OpsForge.Agent"

if [[ ! -f "${INSTALL_DIR}/appsettings.json" ]]; then
  if [[ -f "${CONFIG_SOURCE}" ]]; then
    cp "${CONFIG_SOURCE}" "${INSTALL_DIR}/appsettings.json"
  else
    echo "Config source not found: ${CONFIG_SOURCE}" >&2
    exit 1
  fi
fi

install -o root -g root -m 0644 linux/opsforge-agent.service /etc/systemd/system/opsforge-agent.service

systemctl daemon-reload
systemctl enable opsforge-agent.service

echo
echo "Installed OpsForge Agent to ${INSTALL_DIR}"
echo "Edit ${INSTALL_DIR}/appsettings.json and/or set OPSFORGE_AGENT_ENROLLMENT_TOKEN."
echo "Then start it with:"
echo "  sudo systemctl start opsforge-agent"
echo "Inspect with:"
echo "  systemctl status opsforge-agent"
echo "  journalctl -u opsforge-agent -f"
