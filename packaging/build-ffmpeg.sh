#!/usr/bin/env bash
# Builds the ffmpeg the installers ship next to sakshi: LGPL, no external libraries, from the official source.
#
# Usage: packaging/build-ffmpeg.sh RID OUTDIR
#   RID: linux-x64 (on Linux), osx-arm64 (on macOS), win-x64 (cross-compiled on Linux; needs mingw-w64)
#   x86-64 targets need nasm.
# OUTDIR gets ffmpeg (ffmpeg.exe on Windows) and ffmpeg-COPYING.LGPLv2.1.txt.
set -euo pipefail

VERSION=9.1
SHA256=1a4d394ae63d190a526cd052eb4415c156b5c5e2df37efab41a7de0b8fde2433

rid=$1
mkdir -p "$2"
out=$(cd "$2" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

curl -fsSL -o "$work/ffmpeg.tar.xz" "https://ffmpeg.org/releases/ffmpeg-$VERSION.tar.xz"
echo "$SHA256  $work/ffmpeg.tar.xz" | shasum -a 256 -c -
tar -xf "$work/ffmpeg.tar.xz" -C "$work"
cd "$work/ffmpeg-$VERSION"

# --disable-autodetect: nothing from the build machine creeps in, so the binary needs only the OS's own libraries.
flags=(--disable-autodetect --disable-network --disable-doc --disable-ffplay --disable-ffprobe --disable-debug
       --enable-static --disable-shared)
exe=ffmpeg
case "$rid" in
  linux-x64) ;;
  osx-arm64) flags+=(--extra-cflags=-mmacosx-version-min=13.0 --extra-ldflags=-mmacosx-version-min=13.0) ;;
  win-x64)
    flags+=(--arch=x86_64 --target-os=mingw32 --cross-prefix=x86_64-w64-mingw32- --extra-ldflags=-static)
    exe=ffmpeg.exe ;;
  *) echo "unknown RID: $rid" >&2; exit 2 ;;
esac

./configure "${flags[@]}"
# Never ship a GPL or nonfree build by accident.
grep -q '#define FFMPEG_LICENSE "LGPL version 2.1 or later"' config.h || { echo "not an LGPL build" >&2; exit 1; }
make -j"$(getconf _NPROCESSORS_ONLN)"

cp "$exe" "$out/"                                  # make leaves the stripped binary here (ffmpeg_g keeps symbols)
cp COPYING.LGPLv2.1 "$out/ffmpeg-COPYING.LGPLv2.1.txt"
echo "built $out/$exe"
