#!/bin/zsh
# Install dist/AI Room Native.app into /Applications, replacing the previous copy.
# The old copy is moved to the Trash (restorable), never deleted. User data in
# ~/Library/Application Support/AIROOMNative and the Keychain key are untouched.
# Usage: ./Scripts/install-app.sh [--open]
set -euo pipefail
cd "$(dirname "$0")/.."

SOURCE="$PWD/dist/AI Room Native.app"
TARGET="/Applications/AI Room Native.app"
# The app was called AIROOMNative.app before 0.2.108; that copy goes to the Trash too.
LEGACY="/Applications/AIROOMNative.app"
BUNDLE_ID="dev.airoom.native"

if [[ ! -x "$SOURCE/Contents/MacOS/AIROOMNative" ]]; then
  echo "ERROR: $SOURCE not found. Run ./Scripts/package-app.sh first." >&2
  exit 1
fi
/usr/bin/codesign --verify --deep --strict "$SOURCE"

# Quit a running copy so its last state is saved before the bundle is swapped.
if pgrep -f "/Contents/MacOS/AIROOMNative" >/dev/null; then
  echo "Quitting running AI Room Native..."
  /usr/bin/osascript -e "tell application id \"$BUNDLE_ID\" to quit" >/dev/null 2>&1 || true
  for _ in {1..50}; do
    pgrep -f "/Contents/MacOS/AIROOMNative" >/dev/null || break
    sleep 0.2
  done
  if pgrep -f "/Contents/MacOS/AIROOMNative" >/dev/null; then
    echo "ERROR: AI Room Native is still running. Quit it manually and retry." >&2
    exit 1
  fi
fi

if [[ -e "$TARGET" ]]; then
  OLD_VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$TARGET/Contents/Info.plist" 2>/dev/null || echo old)"
  TRASHED="$HOME/.Trash/AIROOMNative-$OLD_VERSION-$(date +%Y%m%d-%H%M%S).app"
  mv "$TARGET" "$TRASHED"
  echo "Moved previous version to Trash: $TRASHED"
fi

if [[ -e "$LEGACY" ]]; then
  LEGACY_VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$LEGACY/Contents/Info.plist" 2>/dev/null || echo old)"
  mv "$LEGACY" "$HOME/.Trash/AIROOMNative-$LEGACY_VERSION-$(date +%Y%m%d-%H%M%S).app"
  echo "Moved the previously named AIROOMNative.app to Trash"
fi

# Move (not copy) so only one copy of the app exists for Spotlight/Launchpad.
mv "$SOURCE" "$TARGET"
/usr/bin/codesign --verify --deep --strict "$TARGET"
echo "Installed: $TARGET ($(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$TARGET/Contents/Info.plist"))"

if [[ "${1:-}" == "--open" ]]; then
  /usr/bin/open "$TARGET"
fi
