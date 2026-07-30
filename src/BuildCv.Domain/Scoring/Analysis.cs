namespace BuildCv.Domain.Scoring;

public class Analysis
{
    public required ScoreBreakdown Breakdown { get; init; }
    public required int OverallScore { get; init; }
    public required ScoreBand Band { get; init; }
    public List<string> Recommendations { get; init; } = [];
}

public enum ScoreBand
{
    Bajo,
    Medio,
    Bueno,
    Fuerte
}
