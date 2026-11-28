#!/usr/bin/env bash
set -eu
cd -- "$(dirname -- "$0")"
mode=${1:-status}
shift || true
case "$mode" in
  general|general-abliterated|video|video-abliterated|status) ;;
  *) echo "Usage: $0 {general|general-abliterated|video|video-abliterated|status} [--dry-run] [--node NAME]" >&2; exit 2 ;;
esac
only=
preview=false
options=()
while (($#)); do
  case "$1" in
    --dry-run) preview=true; options+=(--dry-run); shift ;;
    --node) only=${2:?missing node}; shift 2 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done
python=python3
"$python" -c "import yaml" 2>/dev/null || python=python
inventory=(inventory --roles)
if ! $preview && [[ $(uname -s) != Darwin ]]; then inventory+=(--remote); fi
members=$("$python" common/modes.py "${inventory[@]}") || exit 1
if $preview; then echo "Offline preview using this checkout's Mac config"; fi
if [[ -n $only ]] && ! cut -f1 <<< "$members" | grep -Fxq -- "$only"; then
  echo "$only is outside the enabled Mac inventory" >&2; exit 2
fi
while IFS=$'\t' read -r node role <&3; do
  [[ -n $node ]] || continue
  if [[ -n $only && $node != "$only" ]]; then continue; fi
  case "$role:$mode" in
    gateway:status|edge:status) ;;
    gateway:*) echo "$node: LiteLLM router"; continue ;;
  esac
  args=("$mode")
  if [[ $mode != status ]]; then
    # The worker resolves runtime and catalog from its own YAML.
    args=(general --profile "$mode" --quiesced)
  fi
  "$python" common/modes.py "${args[@]}" --node "$node" --remote ${options[@]+"${options[@]}"}
done 3<<< "$members"
