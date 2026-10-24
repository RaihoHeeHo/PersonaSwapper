#!/bin/bash
# Double-click in Finder to build (first time) and run Persona Swapper.
cd "$(dirname "$0")"
if ! command -v dotnet >/dev/null 2>&1; then
  export PATH="/usr/local/share/dotnet:/opt/homebrew/bin:$PATH"
fi
if ! command -v dotnet >/dev/null 2>&1; then
  echo "The .NET 8 SDK isn't installed. Install it with: brew install --cask dotnet-sdk"
  read -n 1 -s -r -p "Press any key to close."
  exit 1
fi
if [ ! -d "../GFD-Studio-macOS/GFDLibrary" ]; then
  echo "GFD-Studio-macOS must be in the same folder as PersonaSwapper (it provides GFDLibrary)."
  read -n 1 -s -r -p "Press any key to close."
  exit 1
fi
dotnet run -c Release --project PersonaSwapper.App
