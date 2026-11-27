#!/usr/bin/env bash
# Purpose: Run APT update, upgrade and autoremove on Debian/Ubuntu/Mint.
# Usage:   sudo ./setup-os-maintenance.sh                 # run now
#          sudo ./setup-os-maintenance.sh --install-timer # daily at 03:00
#          sudo ./setup-os-maintenance.sh --remove-timer  # disable schedule
#          ./setup-os-maintenance.sh --help
# Schedule: change OnCalendar below; rerun --install-timer to update it.
# Logs:    journalctl -u daily-apt-maintenance.service
# Safety:  stops on error; keeps existing dpkg config files; no explicit reboot.
set -euo pipefail

name=daily-apt-maintenance
installed=/usr/local/sbin/$name
units=/etc/systemd/system

usage() { sed -n '2,9p' "$0" | sed 's/^# *//'; }
case "${1:-}" in
  --help|-h) usage; exit 0 ;;
  ''|--run|--install-timer|--remove-timer) ;;
  *) usage >&2; exit 2 ;;
esac
if (( $# > 1 )); then usage >&2; exit 2; fi
if (( EUID != 0 )); then echo "Run with sudo." >&2; exit 1; fi

case "${1:-}" in
  --install-timer)
    command -v systemctl >/dev/null
    # Use a root-owned copy, not a mutable script in a user's Git checkout.
    if [[ $(readlink -f "$0") != "$installed" ]]; then
      install -o root -g root -m 0755 "$0" "$installed"
    fi
    cat > "$units/$name.service" <<'SERVICE'
[Unit]
Description=Daily APT maintenance
Wants=network-online.target
After=network-online.target

[Service]
Type=oneshot
ExecStart=/usr/local/sbin/daily-apt-maintenance --run
SERVICE
    cat > "$units/$name.timer" <<'TIMER'
[Unit]
Description=Daily APT maintenance schedule

[Timer]
OnCalendar=*-*-* 03:00:00
RandomizedDelaySec=30m
Persistent=true

[Install]
WantedBy=timers.target
TIMER
    systemctl daemon-reload
    systemctl enable "$name.timer"
    systemctl restart "$name.timer"
    systemctl list-timers "$name.timer" --no-pager
    exit 0 ;;
  --remove-timer)
    if [[ -f "$units/$name.timer" ]]; then
      systemctl disable --now "$name.timer"
      rm -f "$units/$name.timer" "$units/$name.service"
      systemctl daemon-reload
    fi
    echo "Daily schedule removed. Running maintenance, if any, was not interrupted."
    exit 0 ;;
esac

command -v apt-get >/dev/null
command -v flock >/dev/null
export DEBIAN_FRONTEND=noninteractive
export NEEDRESTART_MODE=l
# APT also uses its own locks for other package managers.
exec 9>/run/lock/daily-apt-maintenance.lock
if ! flock -n 9; then echo "Maintenance already running."; exit 0; fi
APT=(apt-get -o DPkg::Lock::Timeout=300 -o Acquire::Retries=3
     -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold)
trap 'echo "Maintenance failed at line $LINENO (exit $?)." >&2' ERR
printf '\n=== Maintenance started: %s ===\n' "$(date)"
"${APT[@]}" update --error-on=any
"${APT[@]}" upgrade --with-new-pkgs -y
"${APT[@]}" autoremove -y
df -h /
if [[ -f /var/run/reboot-required ]]; then
  echo 'NOTICE: A reboot is required; reboot manually when convenient.'
fi
printf '\n=== Maintenance completed: %s ===\n' "$(date)"
