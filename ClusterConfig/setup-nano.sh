#!/usr/bin/env bash
# setup-nano.sh — reconcile Ollama on JetPack using config/nano.yaml.
# Default/--models installs missing runtime/models and validates real inference.
# --models --prune / --cleanup removes obsolete adopted or listed tags.
# --validate, --benchmark and --health test the existing runtime.
# Run as normal login user; --help lists options. Vendor drivers stay intact.
set -euo pipefail
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
exec bash "$HERE/common/setup.sh" nano "$@"
