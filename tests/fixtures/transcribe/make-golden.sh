#!/usr/bin/env bash
# Regenerates expected.md for the meeting.md golden tests.
#
# It extracts the Python heredoc (<<'PYEOF' ... PYEOF) from tests/fixtures/bash/transcribe-meeting (a copy of the bash tool's script),
# the behavioural spec for MeetingMarkdown, and runs it, with dedupe off, on a
# scratch copy of each fixture directory:
#   frames/    transcript.json + frames/raw_000{1..3}.jpg + frames/.metadata.txt
#   noframes/  transcript.json, rendered with NO_FRAMES=1
#   empty/     {"transcription": []}, no frames
# The meeting.md each run produces is stored as <dir>/expected.md. Never edit
# expected.md by hand; edit the inputs and rerun this script.
#
# The source file name is passed to Python as "weekly.sync.mp4" (MeetingMarkdownTests
# uses the same name): the stem must keep the inner dot.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
spec="$here/../bash/transcribe-meeting"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

sed -n "/<<'PYEOF'/,/^PYEOF\$/p" "$spec" | sed '1d;$d' > "$work/render.py"

render() {   # <fixture dir> <no_frames 0|1>
    local name="$1" no_frames="$2"
    rm -rf "$work/run" && cp -R "$here/$name" "$work/run"
    rm -f "$work/run/expected.md"
    python3 "$work/render.py" "$work/run" "weekly.sync.mp4" "$no_frames" off > /dev/null
    cp "$work/run/meeting.md" "$here/$name/expected.md"
}

render frames 0
render noframes 1
render empty 0
