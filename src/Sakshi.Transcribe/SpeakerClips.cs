using System.Text.RegularExpressions;

namespace Sakshi.Transcribe;

/// <summary>The sound clips in a meeting's speakers folder: S3.wav, or S3-2.wav when sakshi speakers --add could not
/// replace an S3.wav that was open in another program.</summary>
public static partial class SpeakerClips
{
    [GeneratedRegex(@"^S\d+(?:-\d+)?\.wav$")]
    private static partial Regex ClipName();

    public static bool IsClipName(string fileName) => ClipName().IsMatch(fileName);
}
