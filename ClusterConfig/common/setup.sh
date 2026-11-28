#!/usr/bin/env bash
# Shared launcher. Uses OS Python + PyYAML, or uv's isolated Python environment.
# No GPU packages or application services are installed by this launcher.
# If neither option is present, install a current Python + PyYAML (Linux) or uv.
set -euo pipefail
export PATH="/opt/homebrew/bin:/usr/local/bin:$HOME/.local/bin:$PATH"
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
[[ $(id -u) != 0 ]] || { echo 'Run as your normal login user, not root.' >&2; exit 1; }
entry="$HERE/manage.py"
uv_options=()
case "${1:-}" in
  --inventory)
    entry="$HERE/modes.py"
    shift
    set -- inventory --roles "$@"
    ;;
  --modes)
    entry="$HERE/modes.py"
    shift
    case "${1:-}" in
      provision-*|download-media) ;;
      *) uv_options=(--offline) ;;
    esac
    ;;
esac
for interpreter in python3 python; do
  if command -v "$interpreter" >/dev/null && "$interpreter" -c 'import sys, yaml, tarfile; assert sys.version_info >= (3, 9) and hasattr(tarfile, "data_filter")' >/dev/null 2>&1; then
    exec "$interpreter" "$entry" "$@"
  fi
done
if command -v uv >/dev/null; then
  if [[ ${#uv_options[@]} -gt 0 ]]; then
  exec uv run "${uv_options[@]}" --no-project --python 3.12 --with pyyaml==6.0.3 python "$entry" "$@"
else
  exec uv run --no-project --python 3.12 --with pyyaml==6.0.3 python "$entry" "$@"
fi
fi
echo 'Install Python with PyYAML or uv on this machine (Mac: brew install uv).' >&2
exit 1
