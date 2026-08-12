namespace BuildCv.Domain.Candidates;

public sealed record CandidateProfileId
{
    public Guid Value { get; }

    public CandidateProfileId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Candidate profile id cannot be empty.", nameof(value));

        Value = value;
    }

    public static CandidateProfileId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
