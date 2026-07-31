namespace BuildCv.Domain.Scoring;

public sealed record ScoringWeightsSnapshot(
    double Skills,
    double Experience,
    double Education,
    double Certifications,
    double Projects);
