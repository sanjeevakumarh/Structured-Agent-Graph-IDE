#!/usr/bin/env bash
set -eu
cd -- "$(dirname -- "$0")"
mode=${1:-status}
shift || true
case "$mode" in
  general|general-abliterated|video|video-abliterated|status) ;;
  *) echo "Usage: $0 {general|general-abliterated|video|video-abliterated|status} [--dry-run] [--node NAME]" >&2; exit 2 ;;
esac
nodes=(gmini p520 z820)
options=()
while (($#)); do
  case "$1" in
    --dry-run) options+=(--dry-run); shift ;;
    --node) nodes=("${2:?missing node}"); shift 2 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done
python=python3
"$python" -c "import yaml" 2>/dev/null || python=python
for node in "${nodes[@]}"; do
  args=("$mode")
  if [[ $mode != status ]]; then
    # The worker resolves runtime and catalog from its own YAML.
    args=(general --profile "$mode" --quiesced)
  fi
  "$python" common/modes.py "${args[@]}" --node "$node" --remote ${options[@]+"${options[@]}"}
done
