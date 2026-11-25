namespace Sakshi.Speakers;

/// <summary>One stretch of one voice. Piece is the clustering label within a single recording.</summary>
public sealed record PieceTurn(double Start, double End, int Piece);

public interface IDiarizer : IDisposable
{
    /// <summary>Finds who spoke when in 16 kHz mono samples.</summary>
    IReadOnlyList<PieceTurn> Diarize(float[] samples, CancellationToken ct);
}
