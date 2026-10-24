#!/bin/bash
# Double-click in Finder to build Persona Swapper as standalone apps:
#   release/macOS-Apple-Silicon/Persona Swapper.app   (M1/M2/M3/M4 Macs)
#   release/macOS-Intel/Persona Swapper.app           (Intel Macs)
#   release/Windows/PersonaSwapper.exe                 (64-bit Windows, single file)
# plus a .zip of each for sharing. None of them need .NET installed.
# Needs: the .NET SDK (brew install --cask dotnet-sdk) and an internet connection the first time.
set -euo pipefail
cd "$(dirname "$0")"

# On GitHub Actions (CI=true) there's nobody to press a key or look at Finder.
pause() { [ -n "${CI:-}" ] || { read -n 1 -s -r -p "Press any key to close."; echo; }; }
trap 'echo; echo "Build failed - see the messages above."; pause; exit 1' ERR

if ! command -v dotnet >/dev/null 2>&1; then
  export PATH="/usr/local/share/dotnet:/opt/homebrew/bin:$PATH"
fi
if ! command -v dotnet >/dev/null 2>&1; then
  echo "The .NET SDK isn't installed. Install it with: brew install --cask dotnet-sdk"
  pause; exit 1
fi
if [ ! -d "../GFD-Studio-macOS/GFDLibrary" ]; then
  echo "GFD-Studio-macOS must be in the same folder as PersonaSwapper (it provides GFDLibrary)."
  pause; exit 1
fi

PROJECT="PersonaSwapper.App/PersonaSwapper.App.csproj"
# VERSION can be passed in (the release workflow uses the tag, e.g. v1.2.0 -> 1.2.0).
VERSION=${VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT")}
OUT="$(pwd)/release"
WORK="$OUT/.work"
rm -rf "$OUT"
mkdir -p "$WORK"

publish() {
  local rid=$1; shift
  echo "==> Building for $rid"
  dotnet publish "$PROJECT" -c Release -r "$rid" --self-contained true \
    -p:DebugType=none -p:DebugSymbols=false -p:Version="$VERSION" -o "$WORK/$rid" "$@"
}

publish osx-arm64
publish osx-x64
publish win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

echo "==> Making the app icon"
ICONSET="$WORK/AppIcon.iconset"
mkdir -p "$ICONSET"
SRC_ICON="PersonaSwapper.App/Assets/icon-1024.png"
for size in 16 32 128 256 512; do
  sips -z $size $size "$SRC_ICON" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  sips -z $((size * 2)) $((size * 2)) "$SRC_ICON" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$WORK/AppIcon.icns"

make_app() {
  local rid=$1 folder=$2
  local app="$OUT/$folder/Persona Swapper.app"
  echo "==> Packaging $folder"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  cp -R "$WORK/$rid/." "$app/Contents/MacOS/"
  cp "$WORK/AppIcon.icns" "$app/Contents/Resources/AppIcon.icns"
  cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Persona Swapper</string>
  <key>CFBundleDisplayName</key><string>Persona Swapper</string>
  <key>CFBundleIdentifier</key><string>io.github.personaswapper</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleExecutable</key><string>PersonaSwapper</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
PLIST
  chmod +x "$app/Contents/MacOS/PersonaSwapper"
  # Apple Silicon refuses to run unsigned code; an ad-hoc signature is enough on this Mac.
  codesign --force --deep --sign - "$app"
  codesign --verify --deep --strict "$app"
  (cd "$OUT/$folder" && ditto -c -k --keepParent "Persona Swapper.app" "$OUT/PersonaSwapper-$VERSION-$folder.zip")
}

make_app osx-arm64 "macOS-Apple-Silicon"
make_app osx-x64 "macOS-Intel"

echo "==> Packaging Windows"
mkdir -p "$OUT/Windows"
cp "$WORK/win-x64/PersonaSwapper.exe" "$OUT/Windows/PersonaSwapper.exe"
(cd "$OUT/Windows" && zip -q "$OUT/PersonaSwapper-$VERSION-Windows-x64.zip" PersonaSwapper.exe)

rm -rf "$WORK"
echo
echo "Done. Builds are in: $OUT"
ls -1 "$OUT"
[ -n "${CI:-}" ] || open "$OUT"
pause
