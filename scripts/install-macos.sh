#!/usr/bin/env bash
# Builds Mission Control from this checkout and installs it for the current user on macOS:
# the app goes to /Applications and the memory plugin is registered with Claude Code and/or Codex.
# Rerun after `git pull` to update; history, settings and project approvals are kept.
set -euo pipefail
cd "$(dirname "$0")/.."

host=both
while [ $# -gt 0 ]; do
  case "$1" in
    --host) host="${2:-}"; shift 2 ;;
    *) echo "Usage: scripts/install-macos.sh [--host claude|codex|both]" >&2; exit 2 ;;
  esac
done
case "$host" in claude|codex|both) ;; *) echo "--host must be claude, codex or both." >&2; exit 2 ;; esac

[ "$(uname -s)" = Darwin ] || { echo "Run this on a Mac." >&2; exit 1; }
major=$(sw_vers -productVersion | cut -d. -f1)
[ "$major" -ge 15 ] || { echo "Mission Control needs macOS 15 or newer (this Mac has $(sw_vers -productVersion))." >&2; exit 1; }

missing=()
xcode-select -p >/dev/null 2>&1 || missing+=("Xcode command-line tools: xcode-select --install")
command -v dotnet >/dev/null 2>&1 || missing+=(".NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0")
command -v cargo >/dev/null 2>&1 || missing+=("Rust: https://rustup.rs")
command -v npm >/dev/null 2>&1 || missing+=("Node.js 22 or newer: https://nodejs.org")
command -v python3 >/dev/null 2>&1 || missing+=("Python 3: xcode-select --install provides it")
if [ ${#missing[@]} -gt 0 ]; then
  echo "Install these first, then run this script again:" >&2
  printf '  - %s\n' "${missing[@]}" >&2
  exit 1
fi

case "$(uname -m)" in
  arm64) arch=arm64; target=aarch64-apple-darwin ;;
  x86_64) arch=x64; target=x86_64-apple-darwin ;;
  *) echo "Unsupported Mac architecture: $(uname -m)" >&2; exit 1 ;;
esac

app="/Applications/Mission Control.app"
if pgrep -f "$app/Contents/MacOS/" >/dev/null 2>&1; then
  echo "Quit Mission Control first (menu bar icon > Quit completely), then run this again." >&2
  exit 1
fi

echo "Building Mission Control for $arch (the first build takes a while)..."
command -v rustup >/dev/null 2>&1 && rustup target add "$target" >/dev/null
npm ci --prefix ui
node scripts/build-tauri.mjs --arch "$arch"

built="src-tauri/target/$target/release/bundle/macos/Mission Control.app"
[ -d "$built" ] || { echo "The build did not produce $built." >&2; exit 1; }
rm -rf "$app"
cp -R "$built" "$app"
echo "Installed $app"

# Setup checks the memory server against the workspace the app creates on its first start.
data="$HOME/Library/Application Support/MissionControl"
open "$app"
for _ in $(seq 1 60); do [ -f "$data/live/workspace.db" ] && break; sleep 1; done
[ -f "$data/live/workspace.db" ] || { echo "Mission Control did not create its workspace; open it once, then rerun this script." >&2; exit 1; }
python3 "$app/Contents/Resources/integrations/setup.py" --install --host "$host"

cat <<'EOF'

Mission Control is installed. Next:
  1. In Mission Control, open Settings > Monitoring and enable your Claude Code / Codex session folders.
  2. In Settings > Approved projects, approve the projects it should remember. Nothing is read before that.
  3. Restart your Claude Code / Codex sessions so they load the memory plugin.
EOF
