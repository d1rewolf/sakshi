namespace Sakshi.Transcribe;

public static class TranscribeUsage
{
    /// <summary>The text of transcribe-meeting's usage(), with this program's name.</summary>
    public const string Text = """
        Usage: sakshi transcribe [options] <input-video>

        Input is anything ffmpeg can read: .mp4, .mov (iPhone/QuickTime, rotation
        honored), .mkv, .webm, or audio-only files like .m4a/.mp3/.wav (use -F none).

        Options:
          -o, --output DIR        Output directory (created if missing); defaults to
                                  the video filename without extension, spaces
                                  replaced with underscores, in the current directory
          -t, --threshold NUM     Scene-change threshold 0.0-1.0; moderate changes above
                                  this are kept if min-gap has elapsed (default: 0.2)
          -T, --strong NUM        Strong scene-change threshold; changes above this are
                                  always kept, ignoring min-gap (default: 0.45)
          -g, --min-gap SECS      Minimum seconds between moderate frames (default: 8)
          -F, --frames MODE       Frame extraction mode (default: scene)
                                    scene    scene-change detection tuned by -t/-T/-g
                                    changes  one frame each time the picture has held
                                             still for a second (a slide, a shared
                                             screen); -t/-T/-g ignored
                                    all      every single frame — can produce a huge
                                             number of files (-t/-T/-g ignored)
                                    none     no frames at all; meeting.md is the
                                             transcript only (-t/-T/-g/-D ignored)
          -D, --dedupe [N]        After extraction, drop frames whose 256-bit dHash is
                                  within Hamming distance N (default: 8) of any
                                  earlier kept frame
          -m, --model PATH        whisper.cpp model file (default: ggml-base.en.bin,
                                  downloaded on first use)
          -w, --max-width PX      Max frame width; smaller frames untouched (default: 1280)
          -l, --language LANG     Spoken language for whisper, "auto" to detect (default: en)
          -c, --max-context N     Tokens of the previous window's transcript that
                                  whisper carries into the next one (default: 0).
                                  0 transcribes each window on its own, which stops
                                  the repetition loops whisper falls into over long
                                  silences, where one phrase gets stuck and repeats
                                  for minutes. Pass -1 to carry the full context: it
                                  reads more fluently on clean continuous speech, but
                                  can run away on meetings.
          -M, --markdown-only     Like -F none, but write the markdown to
                                  <video name>.md (spaces replaced with underscores) in
                                  the current directory and keep no other artifacts;
                                  -o is ignored
          -h, --help              Show this help

        Outputs into <output-dir>:
          audio.wav, transcript.srt, transcript.json,
          frames/frame_HH-MM-SS.jpg, meeting.md

        Choosing a frame mode (rule of thumb):
          The transcript carries most of the meaning; frames are exhibits. An LLM
          reading meeting.md wants every distinct screen exactly once — each image
          costs ~1k tokens, so redundancy crowds out transcript.

          -F changes -D 8    Best for slide decks / screen shares fed to an LLM:
                             keeps a frame each time the picture settles for a
                             second, then the global 256-bit dedupe collapses
                             revisited slides to one frame each. Webcam motion
                             does not add frames; a slide shown for under a
                             second is skipped.
          -F scene           (default) Best for content with continuous motion —
                             live dashboards, maps, videos — that never holds
                             still, so 'changes' would skip it. Samples on scene
                             cuts with a min-gap instead; lossy but bounded.
          -F all             Debugging only. Never commit its output to git.
          -F none            Audio-only meetings, podcasts, calls with nothing worth
                             looking at. Skips the video pass entirely, so it is also
                             the fastest.

          If frames/ comes back with more than ~300 files, the content is animated:
          rerun with -F scene, or raise -D toward 16 and accept sampling.

        Examples:
          sakshi transcribe -F changes -D 8 meeting.mp4      # accuracy-first, LLM-ready
          sakshi transcribe -o notes -F scene meeting.mp4    # bounded output for live demos
          sakshi transcribe -F none -o notes meeting.mp4     # transcript only, keeps audio/srt
          sakshi transcribe -M meeting.mp4                   # transcript only, single .md file
        """;
}
