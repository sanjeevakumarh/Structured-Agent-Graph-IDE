#!/usr/bin/env bash
# setup-macmini.sh — reconcile the macmini profile in config/macmini.yaml.
# Edit that YAML for models, endpoints and backend; versions in config/versions.yaml.
# Run with --help for actions; --dry-run previews; --models syncs; --cleanup prunes
# obsolete owned artifacts; --reset rebuilds runtime without deleting weights.
# Run as normal login user, not root. Sudo manages the system launchd service.
# --migrate retires inventory-listed legacy assets only after smoke tests.
# OS drivers, partitions, unrelated files and edge vendor services are preserved.
set -euo pipefail
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
exec bash "$HERE/common/setup.sh" macmini "$@"
