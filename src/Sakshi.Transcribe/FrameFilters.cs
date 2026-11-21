using System.Globalization;

namespace Sakshi.Transcribe;

public static class FrameFilters
{
    /// <summary>The ffmpeg -vf string for the options' frame mode (not valid for FramesMode.None).</summary>
    public static string VideoFilter(TranscribeOptions o, string metadataFile)
    {
        var mode = o.Frames switch
        {
            FramesMode.Scene =>
                "select='isnan(prev_selected_t)"
                + $"+gt(scene\\,{Num(o.Strong)})"
                + $"+gt(scene\\,{Num(o.Threshold)})*gte(t-prev_selected_t\\,{Num(o.MinGap)})'",
            FramesMode.Changes => "mpdecimate,metadata=mode=add:key=keep:value=1",
            FramesMode.All => "metadata=mode=add:key=keep:value=1",
            _ => throw new ArgumentException("No video filter for frame mode " + o.Frames, nameof(o)),
        };
        var scale = $"scale=w='min({Num(o.MaxWidth)}\\,iw)':h=-2";
        return $"{mode},{scale},metadata=print:file={metadataFile}";
    }

    private static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);
}
