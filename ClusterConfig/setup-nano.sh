#!/usr/bin/env bash
# Seed/reconcile this node using its node profile and config/versions.yaml.
# Default: delete old inference assets, deploy the YAML catalog and validate.
# --dry-run previews; --models syncs; --health/--validate check inference.
# --calibrate/--benchmark measure performance; --help lists available actions.
# No rollback. Run as normal login user. OS drivers and unrelated files stay.
set -euo pipefail
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
node=$(basename -- "${BASH_SOURCE[0]}" .sh)
exec bash "$HERE/common/setup.sh" "${node#setup-}" "$@"
