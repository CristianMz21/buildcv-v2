namespace BuildCv.Application.Resumes;

/// <summary>
/// Maps a country named in a candidate's own location line to its international dialing code, so a
/// national phone number can be <b>proposed</b> in international form.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists to make a suggestion, never to store a value.</b> `310 4580645` is a real Colombian
/// mobile and a perfectly ordinary way to write one, but as stored data it is incomplete — nothing in
/// those digits says which country. Accepting it would put a number in the database that cannot be
/// dialled and that nobody chose; refusing it, which is what happens today, hands a correct extraction
/// back to the candidate as an error. Proposing `+57 …` and letting them confirm is the only one of the
/// three that neither loses the reading nor invents the fact.
/// </para>
/// <para>
/// <b>Scoped to this product's market rather than to the world.</b> BuildCv is aimed at Spanish-speaking
/// job seekers, so the table covers Latin America and Spain plus the two destinations that dominate its
/// diaspora. A country outside it yields no hint and therefore no suggestion — which is the correct
/// outcome, not a gap: a wrong prefix silently accepted is worse than a field the candidate completes.
/// </para>
/// <para>
/// <b>The location is evidence, not proof, and that is why it only ever suggests.</b> Somebody who
/// emigrated may live in one country and carry another country's number; the review screen shows the
/// proposal, they change it, and nothing was decided for them.
/// </para>
/// </remarks>
public static class PhoneCountryHints
{
    // Keys are already normalised the way ResumeSectionHeadings.Normalize normalises: lowercase, no
    // diacritics. "mexico" therefore matches "México" and "MEXICO" without three entries.
    private static readonly IReadOnlyDictionary<string, string> CodesByCountry =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["colombia"] = "57",
            ["mexico"] = "52",
            ["argentina"] = "54",
            ["chile"] = "56",
            ["peru"] = "51",
            ["espana"] = "34",
            ["spain"] = "34",
            ["ecuador"] = "593",
            ["venezuela"] = "58",
            ["uruguay"] = "598",
            ["paraguay"] = "595",
            ["bolivia"] = "591",
            ["costa rica"] = "506",
            ["panama"] = "507",
            ["guatemala"] = "502",
            ["honduras"] = "504",
            ["el salvador"] = "503",
            ["nicaragua"] = "505",
            ["cuba"] = "53",
            ["brasil"] = "55",
            ["brazil"] = "55",
            // +1 with an area code inside the national number, so prefixing it is correct for these too.
            ["republica dominicana"] = "1",
            ["puerto rico"] = "1",
            ["estados unidos"] = "1",
            ["united states"] = "1",
            ["usa"] = "1",
            ["canada"] = "1",
        };

    /// <summary>
    /// The dialing code for a country named anywhere in <paramref name="location"/>, or null.
    /// </summary>
    /// <remarks>
    /// Matched as a whole segment rather than a substring: a location is written "Bogotá, Colombia" or
    /// "Remote — Chile", so the country is one comma-or-dash separated part. Substring matching would
    /// read "Cuba" out of "Cubatão" and hand somebody the wrong country by coincidence.
    /// </remarks>
    public static string? DialingCodeFor(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return null;

        foreach (var segment in location.Split([',', '-', '–', '—', '|', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = ResumeSectionHeadings.Normalize(segment);
            if (CodesByCountry.TryGetValue(normalized, out var code))
                return code;
        }

        return null;
    }
}
