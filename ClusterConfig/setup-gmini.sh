#!/usr/bin/env bash
# setup-gmini.sh — reconcile the gmini profile in config/gmini.yaml.
# Edit that YAML for models, endpoints and backend; versions in config/versions.yaml.
# Run with --help for actions; --dry-run previews; --models syncs; --cleanup prunes
# obsolete owned artifacts; --reset rebuilds runtime without deleting weights.
# Run as normal login user, not root. Sudo is only used for managed Linux services.
# --calibrate measures context/cache; --apply edits YAML; deploy separately.
# Setup auto-migrates legacy services; listed old assets retire after inference tests.
# Calibration/benchmark temporarily pause known GPU services and restore them.
# OS drivers, partitions, unrelated files and edge vendor services are preserved.
set -euo pipefail
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
exec bash "$HERE/common/setup.sh" gmini "$@"
