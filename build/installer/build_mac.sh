#!/bin/bash
#
# Build the macOS client end-to-end:
#   1. Publish a self-contained build for one architecture (no .NET on the till)
#   2. Assemble VvCash.app around it, stamped with the version from VvCash.csproj
#   3. Ad-hoc sign the bundle
#   4. Emit two artifacts: a .zip for auto-update and a .dmg for manual installation
#
# Usage:
#   ./build/installer/build_mac.sh                # arm64, the default
#   ./build/installer/build_mac.sh -a x64         # Intel
#   VVCASH_CODESIGN_ID="Developer ID Application: Acme (TEAMID)" ./build/installer/build_mac.sh
#
# This is the macOS counterpart to build_installer.ps1, and it deliberately mirrors that
# script's version handling: the csproj is the single source of the product version, and
# a version that fails to survive the build is caught here rather than in the field.
#
# ---------------------------------------------------------------- why two artifacts
#
# The .dmg and the .zip hold the same signed bundle and differ only in how they reach the
# register, which is the whole point.
#
# A .dmg arrives through a browser, so LaunchServices stamps it with the
# com.apple.quarantine attribute and Gatekeeper refuses an unsigned app outright. Whoever
# sets a register up clears that once, by hand -- see the note this script prints at the
# end. That is acceptable for a one-off installation and unacceptable for every update.
#
# The .zip is fetched by the app's own HttpClient. Nothing in that path sets the
# quarantine attribute -- it is written by LaunchServices for browser, Mail and AirDrop
# downloads, not by the kernel for any write to disk -- so Gatekeeper never assesses the
# update and the ad-hoc signature is enough to run. That is what makes auto-update work
# on macOS without an Apple Developer ID.
#
# ---------------------------------------------------------------- why macOS only
#
# The .NET SDK ad-hoc signs the apphost it produces, and it can only do that on a macOS
# host, where codesign exists. An osx-arm64 build cross-published from Windows or Linux
# yields an unsigned Mach-O, and Apple Silicon refuses to execute one: the register would
# report "Killed: 9" with nothing else to go on. So this script refuses to run anywhere
# else rather than produce an artifact that looks fine and cannot start.

set -euo pipefail

# ---------------------------------------------------------------- arguments

ARCH="arm64"

while [ $# -gt 0 ]; do
    case "$1" in
        -a|--arch)
            ARCH="${2:-}"
            shift 2
            ;;
        -h|--help)
            sed -n '2,40p' "$0" | sed 's/^# \{0,1\}//'
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            echo "Usage: $0 [-a arm64|x64]" >&2
            exit 1
            ;;
    esac
done

case "$ARCH" in
    arm64) RID="osx-arm64" ;;
    x64)   RID="osx-x64" ;;
    *)
        echo "Unknown architecture '$ARCH'. Expected arm64 or x64." >&2
        exit 1
        ;;
esac

# ---------------------------------------------------------------- environment

step() { printf '\033[36m==> %s\033[0m\n' "$1"; }
ok()   { printf '\033[32m    OK  %s\033[0m\n' "$1"; }
warn() { printf '\033[33m    !!  %s\033[0m\n' "$1"; }
die()  { printf '\033[31mERROR: %s\033[0m\n' "$1" >&2; exit 1; }

[ "$(uname -s)" = "Darwin" ] || die "This script must run on macOS. See the note at the top of the file: a cross-published apphost is unsigned and will not execute on Apple Silicon."

for tool in dotnet codesign ditto hdiutil sips iconutil plutil shasum; do
    command -v "$tool" >/dev/null 2>&1 || die "'$tool' not found on PATH."
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJ="$ROOT/src/VvCash/VvCash.csproj"
LOGO="$ROOT/src/VvCash/Assets/logo.png"
PUBLISH_DIR="$ROOT/publish/$RID"
OUTPUT_DIR="$SCRIPT_DIR/Output"

# Bundle identity. Changing the name here is the only edit a rename needs; the identifier
# must stay stable across releases, because macOS keys per-app state -- window positions,
# TCC grants, login items -- to it, and a change reads to the system as a different app.
APP_NAME="VvCash"
BUNDLE_ID="io.proffi.vvcash"

[ -f "$PROJ" ] || die "Project not found at $PROJ"

# ---------------------------------------------------------------- version

# Two steps, because each one alone fails silently in a different way. This mirrors
# build_installer.ps1; the reasoning is spelled out there and repeated in brief here.
#
# First, assert the csproj actually declares <Version>. MSBuild cannot answer this: with
# the element absent the SDK supplies an implicit 1.0.0 and reports it indistinguishably
# from a real declaration, so a bad merge would ship every release stamped 1.0.0 forever.
step "Reading the product version from VvCash.csproj"

# -o and a line count rather than grep -c, which counts matching lines: two <Version>
# elements sharing one line is exactly the malformed case worth catching.
DECLARED=$(grep -o '<Version>[^<]*</Version>' "$PROJ" | wc -l | tr -d '[:space:]')
[ "$DECLARED" -ge 1 ] || die "No <Version> element in $PROJ. Add one to the PropertyGroup -- without it MSBuild silently reports the SDK default of 1.0.0."
[ "$DECLARED" -le 1 ] || die "Found $DECLARED <Version> elements in $PROJ. Refusing to guess which one the build uses."

# Second, ask MSBuild for the evaluated value rather than reading the XML node: only
# MSBuild applies Condition evaluation, so a Release-only <Version> would be invisible to
# a plain text match. Evaluate as Release, which is what is published below.
VERSION="$(dotnet msbuild "$PROJ" -getProperty:Version -p:Configuration=Release -nologo | tr -d '[:space:]')"
[ -n "$VERSION" ] || die "MSBuild returned a blank Version for $PROJ."
ok "Product version $VERSION"

# ---------------------------------------------------------------- publish

step "Publishing a self-contained $RID build"

rm -rf "$PUBLISH_DIR"

# No PublishSingleFile, unlike the Windows build. A single-file host on macOS extracts
# itself to a temporary directory on first run, which puts the executing code outside the
# signed bundle and defeats the whole reason the bundle is signed. A .app is already one
# unit as far as the user is concerned, so the flag buys nothing here.
dotnet publish "$PROJ" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -o "$PUBLISH_DIR"

# Confirm the version actually reached the build rather than trusting the value MSBuild
# reported before compiling. The register reads its own version off the assembly at run
# time (AssemblyAppVersionProvider), so this is the number the update check compares --
# not what Info.plist says. deps.json names the assembly as "VvCash/<version>", which is
# that same $(Version) after MSBuild has evaluated it.
grep -q "\"VvCash/$VERSION\"" "$PUBLISH_DIR/VvCash.deps.json" \
    || die "The published assembly is not stamped $VERSION. The version did not survive the build, and registers would compare the wrong number."
ok "Assembly stamped $VERSION"

# ---------------------------------------------------------------- assemble the bundle

step "Assembling $APP_NAME.app"

APP="$OUTPUT_DIR/$APP_NAME.app"
mkdir -p "$OUTPUT_DIR"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp -R "$PUBLISH_DIR"/. "$APP/Contents/MacOS/"

# Debug symbols are a third of the payload and mean nothing on a till. The Windows
# installer leaves them in only because Inno was never told to filter.
rm -f "$APP/Contents/MacOS"/*.pdb

[ -x "$APP/Contents/MacOS/$APP_NAME" ] || die "Published apphost $APP_NAME not found or not executable in the bundle."

# CFBundleShortVersionString is the version a person sees; CFBundleVersion is the build
# number macOS compares when it has two copies of the same app. Both are set to the same
# three-part number because the project has exactly one version and no separate build
# counter -- inventing one here would be a second source of truth to keep in step.
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleExecutable</key>
    <string>$APP_NAME</string>
    <key>CFBundleIconFile</key>
    <string>$APP_NAME</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.business</string>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
PLIST

plutil -lint "$APP/Contents/Info.plist" >/dev/null || die "Generated Info.plist is not valid."
ok "Info.plist written"

# ---------------------------------------------------------------- icon

step "Building the icon"

if [ -f "$LOGO" ]; then
    ICONSET="$OUTPUT_DIR/$APP_NAME.iconset"
    rm -rf "$ICONSET"
    mkdir -p "$ICONSET"
    for size in 16 32 128 256 512; do
        sips -z "$size" "$size" "$LOGO" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
        sips -z "$((size * 2))" "$((size * 2))" "$LOGO" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
    done
    iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/$APP_NAME.icns"
    rm -rf "$ICONSET"
    ok "$APP_NAME.icns from Assets/logo.png"
else
    # Not fatal: an app with a generic icon still sells goods. Failing the release over a
    # missing logo would be the wrong trade.
    warn "Assets/logo.png missing -- shipping without an icon."
fi

# ---------------------------------------------------------------- sign

step "Signing the bundle"

# The SDK signed the apphost alone, before this script wrapped it in a bundle. A bundle
# signature covers Info.plist and Resources too, so it has to be applied here, after
# everything is in place -- signing first and copying afterwards produces a bundle that
# fails verification.
#
# --deep is deprecated for signing real releases and correct for this one: the payload is
# a flat directory of .NET native libraries with no nested bundles to sign in a
# meaningful order, and enumerating them by hand would be a list to keep in step with
# every dependency change.
#
# "-" is the ad-hoc identity: no certificate, no identity, no Gatekeeper standing. It is
# enough to execute on Apple Silicon, which refuses unsigned Mach-O outright, and that is
# all it is for. Set VVCASH_CODESIGN_ID to sign with a real Developer ID once one exists;
# --options runtime is added with it, because the hardened runtime is a prerequisite for
# notarization and is rejected outright under an ad-hoc signature.
IDENTITY="${VVCASH_CODESIGN_ID:--}"
if [ "$IDENTITY" = "-" ]; then
    codesign --force --deep --sign - "$APP"
    ok "Ad-hoc signed (no Developer ID)"
else
    codesign --force --deep --timestamp --options runtime --sign "$IDENTITY" "$APP"
    ok "Signed with $IDENTITY"
    warn "Signing alone is not notarization. Submit the .zip with 'xcrun notarytool submit --wait', then staple the bundle and rebuild the artifacts."
fi

codesign --verify --deep --strict "$APP" || die "The bundle failed its own signature check."

# Read the version back off the assembled bundle rather than trusting the heredoc above.
STAMPED="$(plutil -extract CFBundleShortVersionString raw "$APP/Contents/Info.plist")"
[ "$STAMPED" = "$VERSION" ] || die "Info.plist reports $STAMPED, expected $VERSION."
ok "Bundle reports $STAMPED"

# ---------------------------------------------------------------- artifacts

ZIP="$OUTPUT_DIR/proffi-kassa-$VERSION-$ARCH.zip"
DMG="$OUTPUT_DIR/proffi-kassa-$VERSION-$ARCH.dmg"

step "Packing the update archive"

# ditto, never zip(1). A .app is full of symlinks and extended attributes, and the code
# signature is computed over both. zip(1) flattens symlinks and drops xattrs, so the
# archive it produces unpacks into a bundle that fails codesign and will not launch. The
# updater on the register unpacks this with 'ditto -x -k' for the same reason.
rm -f "$ZIP"
ditto -c -k --keepParent "$APP" "$ZIP"
ok "$(basename "$ZIP")"

step "Building the disk image"

# A staging directory with a symlink to /Applications, which is what makes the window a
# person can drag the app across. hdiutil takes the directory as-is.
DMG_STAGE="$OUTPUT_DIR/dmg-stage"
rm -rf "$DMG_STAGE"
mkdir -p "$DMG_STAGE"
cp -R "$APP" "$DMG_STAGE/"
ln -s /Applications "$DMG_STAGE/Applications"

rm -f "$DMG"
hdiutil create \
    -volname "$APP_NAME $VERSION" \
    -srcfolder "$DMG_STAGE" \
    -ov -format UDZO \
    "$DMG" >/dev/null
rm -rf "$DMG_STAGE"
ok "$(basename "$DMG")"

# ---------------------------------------------------------------- summary

ZIP_SHA="$(shasum -a 256 "$ZIP" | cut -d' ' -f1)"
ZIP_SIZE="$(stat -f%z "$ZIP")"
DMG_SHA="$(shasum -a 256 "$DMG" | cut -d' ' -f1)"
DMG_MB="$(awk -v b="$(stat -f%z "$DMG")" 'BEGIN { printf "%.1f", b / 1048576 }')"

echo
printf '\033[32mmacOS build %s (%s) ready.\033[0m\n' "$VERSION" "$ARCH"
echo
echo "  bundle     $APP"
echo "  update     $ZIP"
echo "             sha256 $ZIP_SHA"
echo "             $ZIP_SIZE bytes"
echo "  install    $DMG ($DMG_MB MB)"
echo "             sha256 $DMG_SHA"
echo

# The manifest is still written by release.ps1 on Windows and has no platform dimension
# yet, so this block exists to be pasted in by hand until it does. Printing it in the
# final shape avoids a transcription error in the one field -- sha256 -- where a mistake
# means every macOS register downloads the update, refuses it, and retries hourly with
# nothing to show the cashier.
echo "Manifest entry for this build:"
echo
cat <<ENTRY
    "$RID": {
      "url": "https://proffi.io/downloads/proffi-kassa-$ARCH.zip",
      "sha256": "$ZIP_SHA",
      "sizeBytes": $ZIP_SIZE
    }
ENTRY
echo
echo "Upload the .zip under the name in that url. The .dmg is for manual installation only."
echo
printf '\033[33mThe .dmg is not notarized, so a browser download is quarantined and Gatekeeper\n'
printf 'will refuse it. Clear it once per register, at setup:\033[0m\n'
echo
echo "  xattr -dr com.apple.quarantine ~/Applications/$APP_NAME.app"
echo
echo "Updates delivered through the app are never quarantined and need no such step."
