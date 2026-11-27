#!/usr/bin/env bash
# Shared launcher. Uses OS Python + PyYAML, or uv's isolated Python environment.
# No GPU packages or application services are installed by this launcher.
# If neither option is present, install python3-yaml (Linux) or uv (macOS).
set -euo pipefail
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
[[ $(id -u) != 0 ]] || { echo 'Run as your normal login user, not root.' >&2; exit 1; }
if command -v python3 >/dev/null && python3 -c 'import yaml' >/dev/null 2>&1; then
  exec python3 "$HERE/manage.py" "$@"
elif command -v uv >/dev/null; then
  exec uv run --no-project --python 3.12 --with pyyaml==6.0.3 python "$HERE/manage.py" "$@"
else
  echo 'Install prerequisite: sudo apt-get install python3-yaml (Linux), or brew install uv (Mac).' >&2
  exit 1
fi
