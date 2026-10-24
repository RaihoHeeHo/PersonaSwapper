#!/bin/bash
# Builds Persona Swapper for Linux as a self-contained folder (.tar.gz) and an AppImage.
#   ./build-linux.sh [linux-x64|linux-arm64]   (default: this machine's architecture)
# Needs the .NET SDK and the GFD-Studio macOS port next to this folder (../GFD-Studio-macOS).
# The AppImage is made only if appimagetool is on PATH (or APPIMAGETOOL points at it).
set -euo pipefail
cd "$(dirname "$0")"

case "$(uname -m)" in aarch64|arm64) native=linux-arm64 ;; *) native=linux-x64 ;; esac
RID=${1:-$native}
case "$RID" in linux-x64) ARCH=x86_64; LABEL=x64 ;; linux-arm64) ARCH=aarch64; LABEL=arm64 ;; *) echo "Unknown RID $RID"; exit 1 ;; esac

PROJECT="PersonaSwapper.App/PersonaSwapper.App.csproj"
VERSION=${VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT")}
OUT="$(pwd)/release"
WORK="$OUT/.work-$RID"
rm -rf "$WORK"
mkdir -p "$WORK"

echo "==> Building for $RID"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
  -p:DebugType=none -p:DebugSymbols=false -p:Version="$VERSION" -o "$WORK/PersonaSwapper"

APP="$WORK/PersonaSwapper"
chmod +x "$APP/PersonaSwapper"
cp PersonaSwapper.App/Assets/icon.png "$APP/persona-swapper.png"
cat > "$APP/install-desktop-entry.sh" <<'SH'
#!/bin/bash
# Adds Persona Swapper to your applications menu (for the current user).
here="$(cd "$(dirname "$0")" && pwd)"
mkdir -p ~/.local/share/applications ~/.local/share/icons/hicolor/256x256/apps
cp "$here/persona-swapper.png" ~/.local/share/icons/hicolor/256x256/apps/persona-swapper.png
cat > ~/.local/share/applications/persona-swapper.desktop <<DESKTOP
[Desktop Entry]
Type=Application
Name=Persona Swapper
Comment=Turn any Persona 5 Royal model into a persona
Exec="$here/PersonaSwapper"
Icon=persona-swapper
Categories=Game;Utility;
Terminal=false
DESKTOP
echo "Added Persona Swapper to your applications menu."
SH
chmod +x "$APP/install-desktop-entry.sh"

echo "==> Packaging tar.gz"
tar -C "$WORK" -czf "$OUT/PersonaSwapper-$VERSION-Linux-$LABEL.tar.gz" PersonaSwapper

TOOL=${APPIMAGETOOL:-$(command -v appimagetool || true)}
if [ -n "$TOOL" ]; then
  echo "==> Packaging AppImage"
  APPDIR="$WORK/PersonaSwapper.AppDir"
  mkdir -p "$APPDIR/usr/lib"
  cp -R "$APP" "$APPDIR/usr/lib/persona-swapper"
  rm -f "$APPDIR/usr/lib/persona-swapper/install-desktop-entry.sh"
  cp PersonaSwapper.App/Assets/icon.png "$APPDIR/persona-swapper.png"
  cat > "$APPDIR/AppRun" <<'SH'
#!/bin/sh
exec "$APPDIR/usr/lib/persona-swapper/PersonaSwapper" "$@"
SH
  chmod +x "$APPDIR/AppRun"
  cat > "$APPDIR/persona-swapper.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=Persona Swapper
Comment=Turn any Persona 5 Royal model into a persona
Exec=PersonaSwapper
Icon=persona-swapper
Categories=Game;Utility;
Terminal=false
DESKTOP
  ARCH=$ARCH "$TOOL" --no-appstream "$APPDIR" "$OUT/PersonaSwapper-$VERSION-Linux-$LABEL.AppImage"
else
  echo "(appimagetool not found; skipping the AppImage)"
fi

rm -rf "$WORK"
echo "Done:"
ls -1 "$OUT" | grep -i linux
