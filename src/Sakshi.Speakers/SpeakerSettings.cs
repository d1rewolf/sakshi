namespace Sakshi.Speakers;

public static class SpeakerSettings
{
    public const double ClusterThreshold = 0.4;
    public const double MinTurnForPrint = 1.5;
    public const double MaxPrintSpeech = 60;
    public const double MinPrintSpeech = 3;
    public const double JoinThreshold = 0.90;
    public const double SuggestThreshold = 0.60;
    public const double Margin = 0.05;
    public const int SnapWords = 4;
    public const double PauseSeconds = 0.6;
    public const double ClipSeconds = 5;
    public const int MaxVoiceprintsPerPerson = 10;
    public const int SeedCount = 40;
    public const int SampleChars = 120;
}
