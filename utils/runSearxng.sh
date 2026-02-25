#!/usr/bin/env bash
# Starts a SearXNG container with JSON + HTML output formats enabled.
#
# - On first run: boots container with default settings, copies settings.yml
#   out, patches only the formats block, then restarts with the patched file.
# - On subsequent runs: reuses the already-patched settings.yml.
# - Exposes SearXNG on http://localhost:8888

set -euo pipefail

CONTAINER_NAME="searxng"
HOST_PORT=8888
TMP_DIR="${TMPDIR:-/tmp}/searxng-config"
SETTINGS_FILE="$TMP_DIR/settings.yml"

# ── Ensure Docker daemon is running ───────────────────────────────────────────
ensure_docker() {
    if docker info > /dev/null 2>&1; then
        return
    fi

    echo "Docker daemon is not running. Attempting to start it..."

    case "$(uname -s)" in
        Darwin)
            # macOS — Docker Desktop is an app bundle
            open -a Docker
            ;;
        Linux)
            if command -v systemctl > /dev/null 2>&1; then
                sudo systemctl start docker
            else
                sudo service docker start
            fi
            ;;
        *)
            echo "ERROR: Unsupported OS '$(uname -s)'." >&2
            exit 1
            ;;
    esac

    local deadline=$(( $(date +%s) + 60 ))
    while [ "$(date +%s)" -lt "$deadline" ]; do
        sleep 3
        if docker info > /dev/null 2>&1; then
            echo "Docker daemon is ready."
            return
        fi
        printf '.'
    done
    echo
    echo "ERROR: Docker daemon did not start within 60 seconds." >&2
    exit 1
}

# ── Patch only the formats block in settings.yml ──────────────────────────────
patch_formats() {
    local path="$1"
    local tmp
    tmp="$(mktemp)"
    local in_formats=false

    while IFS= read -r line || [ -n "$line" ]; do
        if echo "$line" | grep -qE '^\s+formats:'; then
            printf '%s\n' "$line" >> "$tmp"
            printf '    - html\n'  >> "$tmp"
            printf '    - json\n'  >> "$tmp"
            in_formats=true
        elif $in_formats && echo "$line" | grep -qE '^\s+-\s+'; then
            : # skip original format entries — replaced above
        else
            in_formats=false
            printf '%s\n' "$line" >> "$tmp"
        fi
    done < "$path"

    mv "$tmp" "$path"
    echo "Formats patched in: $path"
}

# ── macOS: unlock keychain so Docker's credential helper can access it ────────
# Docker tries to read the keychain even for public image pulls.
# Needed when the session started without GUI interaction (e.g. new terminal window).
unlock_keychain_macos() {
    if [ "$(uname -s)" != "Darwin" ]; then return; fi
    if ! command -v security > /dev/null 2>&1; then return; fi

    local keychain="$HOME/Library/Keychains/login.keychain-db"
    if security show-keychain-info "$keychain" > /dev/null 2>&1; then
        return  # already unlocked
    fi

    echo "macOS keychain is locked — Docker needs it for credentials."
    echo "Enter your login password to unlock:"
    security unlock-keychain "$keychain"
}

# ── Main ──────────────────────────────────────────────────────────────────────
ensure_docker
unlock_keychain_macos

# Remove any existing container so we start clean
if docker ps -a --filter "name=^${CONTAINER_NAME}$" --format '{{.Names}}' | grep -q "^${CONTAINER_NAME}$"; then
    echo "Removing existing container '$CONTAINER_NAME'..."
    docker rm -f "$CONTAINER_NAME" > /dev/null
fi

mkdir -p "$TMP_DIR"

# First run: boot with defaults, copy settings.yml out, stop, patch
if [ ! -f "$SETTINGS_FILE" ]; then
    echo "No settings.yml found — fetching defaults from container..."

    cmd="docker run -d --name $CONTAINER_NAME searxng/searxng:latest"
    echo "Running: $cmd"
    docker run -d --name "$CONTAINER_NAME" searxng/searxng:latest

    echo "Waiting 5 s for container to initialise..."
    sleep 5

    echo "Copying settings.yml from container..."
    docker cp "${CONTAINER_NAME}:/etc/searxng/settings.yml" "$SETTINGS_FILE"

    echo "Stopping init container..."
    docker rm -f "$CONTAINER_NAME" > /dev/null

    patch_formats "$SETTINGS_FILE"
fi

# Start container with patched settings
cmd="docker run -d -p ${HOST_PORT}:8080 --name ${CONTAINER_NAME} -v \"${TMP_DIR}:/etc/searxng:ro\" searxng/searxng:latest"
echo "Running: $cmd"

docker run -d \
    -p "${HOST_PORT}:8080" \
    --name "$CONTAINER_NAME" \
    -v "${TMP_DIR}:/etc/searxng:ro" \
    searxng/searxng:latest

echo "SearXNG started."
echo "  Browse : http://localhost:${HOST_PORT}"
echo "  JSON   : http://localhost:${HOST_PORT}/search?q=test&format=json"
