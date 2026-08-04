namespace BuildCv.Application.Jobs;

using BuildCv.Domain.Jobs;

/// <summary>
/// Proposes skill requirements from the text of a pasted job offer. It PROPOSES, it never commits: the
/// candidate confirms and edits the draft before <see cref="ImportJobOfferHandler"/> creates anything,
/// exactly as the resume side corrects extracted CV text before it becomes an aggregate.
/// </summary>
/// <remarks>
/// A requirement invented here that the candidate does not catch makes the score wrong in a way they
/// cannot see, so the two rules both err toward missing rather than inventing:
/// <list type="number">
/// <item>
/// RECOGNISE, do not guess. It matches a curated vocabulary of common technology NAMES rather than
/// pulling skills out of arbitrary prose, and it matches CASE-SENSITIVELY on word boundaries, longest
/// term first. It will miss a skill it does not know or that was lower-cased — the candidate types those
/// — and that is the safe direction: a miss is visible on the review screen, an invention is not. The
/// residual false positive is a capitalised common word (e.g. "Spring" the season at a sentence start),
/// which the review screen exists to catch.
/// </item>
/// <item>
/// PRIORITY IS ALWAYS THE CONSERVATIVE <see cref="RequirementPriority.NiceToHave"/>, ALWAYS MARKED
/// GUESSED. It deliberately does NOT read "imprescindible" / "required" to promote to
/// <see cref="RequirementPriority.MustHave"/>: that heuristic is wrong often, and being wrong toward
/// MustHave inflates Critical advice — the failure the scoring phase spent a PR preventing. The
/// candidate promotes what is genuinely a must-have.
/// </item>
/// </list>
/// </remarks>
public static class JobRequirementExtractor
{
    // Deliberately conservative and deliberately incomplete. These are technology names a reader would
    // not mistake for ordinary prose; bare single letters ("C", "R") and the most verb-like names ("Go")
    // are left out entirely rather than risk a false requirement. The candidate adds what this does not
    // know. Order does not matter — matching sorts by length so "SQL Server" wins over "SQL" and
    // "JavaScript" is never split into "Java".
    private static readonly string[] KnownTechnologies =
    [
        "C#", ".NET", "ASP.NET", "F#", "C++", "TypeScript", "JavaScript", "Python", "Java", "Kotlin",
        "Swift", "Ruby", "Rust", "Scala", "PHP", "Elixir",
        "React", "Angular", "Vue", "Svelte", "Next.js", "Node.js", "Express", "Django", "Flask",
        "Spring", "Rails", "Laravel", "FastAPI", "Blazor",
        "SQL Server", "PostgreSQL", "MySQL", "MongoDB", "Redis", "SQLite", "Oracle", "Cassandra",
        "Elasticsearch", "SQL",
        "Docker", "Kubernetes", "Terraform", "Ansible", "Jenkins", "GraphQL", "gRPC", "Kafka", "RabbitMQ",
        "AWS", "Azure", "GCP", "Git"
    ];

    public static IReadOnlyList<ProposedRequirement> Extract(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Greedy longest-first with a consumed-span map: once "SQL Server" claims its characters, "SQL"
        // cannot match inside them, and once "JavaScript" claims its, "Java" cannot. Without this a
        // single mention would propose two overlapping skills.
        var consumed = new bool[text.Length];
        var matches = new List<(int Position, string Term)>();

        foreach (var term in KnownTechnologies.OrderByDescending(t => t.Length))
        {
            var from = 0;
            while (from <= text.Length - term.Length)
            {
                var index = text.IndexOf(term, from, StringComparison.Ordinal);
                if (index < 0)
                    break;

                if (IsBoundedMatch(text, index, term.Length) && !SpanConsumed(consumed, index, term.Length))
                {
                    for (var k = index; k < index + term.Length; k++)
                        consumed[k] = true;
                    matches.Add((index, term));
                }

                from = index + 1;
            }
        }

        // One proposal per distinct term, in order of first appearance. Distinct keeps the first, and the
        // list is already ordered by position, so the candidate reads them in the order the offer named
        // them.
        return matches
            .OrderBy(match => match.Position)
            .Select(match => match.Term)
            .Distinct(StringComparer.Ordinal)
            .Select(term => new ProposedRequirement(term, RequirementPriority.NiceToHave, PriorityGuessed: true))
            .ToList();
    }

    // A match counts only when the characters on either side are NOT alphanumeric, so "Java" does not
    // match inside "Javadoc" and "C++11" does not match "C++". A term's own punctuation (the '#' in "C#",
    // the '.' in ".NET") is part of the term and is not treated as a boundary.
    private static bool IsBoundedMatch(string text, int index, int length)
    {
        var before = index - 1;
        var after = index + length;
        var boundedBefore = before < 0 || !char.IsLetterOrDigit(text[before]);
        var boundedAfter = after >= text.Length || !char.IsLetterOrDigit(text[after]);
        return boundedBefore && boundedAfter;
    }

    private static bool SpanConsumed(bool[] consumed, int index, int length)
    {
        for (var k = index; k < index + length; k++)
            if (consumed[k])
                return true;
        return false;
    }
}

/// <summary>
/// One skill requirement proposed from offer text: the recognised skill name, the conservatively
/// defaulted <see cref="Priority"/>, and <paramref name="PriorityGuessed"/> — always <c>true</c> today,
/// because the extractor never reads a priority from the text. It is on the type so a review screen can
/// show the candidate that the priority is a default to promote, and so the field survives if a future
/// extractor ever does read one.
/// </summary>
public sealed record ProposedRequirement(string Skill, RequirementPriority Priority, bool PriorityGuessed);
