namespace Sakshi.Core.Obs;

public sealed class ObsRequestException(int code, string? comment)
    : Exception(comment is { Length: > 0 } ? $"OBS request failed ({code}): {comment}" : $"OBS request failed ({code})")
{
    public int Code { get; } = code;
    public string? Comment { get; } = comment;
}
