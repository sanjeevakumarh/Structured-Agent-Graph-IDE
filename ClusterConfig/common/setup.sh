#!/usr/bin/env bash
# Shared launcher. Uses OS Python + PyYAML, or uv's isolated Python environment.
# No GPU packages or application services are installed by this launcher.
# If neither option is present, install a current Python + PyYAML (Linux) or uv.
set -euo pipefail
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
[[ $(id -u) != 0 ]] || { echo 'Run as your normal login user, not root.' >&2; exit 1; }
if command -v python3 >/dev/null && python3 -c 'import yaml, tarfile; assert hasattr(tarfile, "data_filter")' >/dev/null 2>&1; then
  exec python3 "$HERE/manage.py" "$@"
elif command -v uv >/dev/null; then
  exec uv run --no-project --python 3.12 --with pyyaml==6.0.3 python "$HERE/manage.py" "$@"
else
  echo 'Install a current patched Python + python3-yaml, or uv (Mac: brew install uv).' >&2
  exit 1
fi
