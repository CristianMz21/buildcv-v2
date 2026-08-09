namespace BuildCv.Api.Common;

/// <summary>
/// The refusals <see cref="ResultExtensions.ToHttpResult{T}"/> can produce, stated once.
/// </summary>
/// <remarks>
/// <para>
/// Every route that converts a <c>Result&lt;T&gt;</c> shares one mapping — 403 for <c>"Forbidden."</c>,
/// 404 for a message ending in <c>"not found."</c>, 400 for anything else — so the three belong to the
/// mechanism rather than to any endpoint. Written out per route they would be forty copies of one fact,
/// and the copy somebody edits is how the document starts disagreeing with what the API answers.
/// </para>
/// <para>
/// It states what the CONVERTER can emit, not what a particular handler happens to. A route whose
/// handler never returns <c>"Forbidden."</c> still advertises 403, and that is the honest direction: a
/// client generated from this document handles a refusal that cannot arrive, rather than being
/// surprised by one that can the day a handler grows an ownership check.
/// </para>
/// </remarks>
internal static class OpenApiResultConventions
{
    public static TBuilder ProducesResultProblems<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// Adds the two refusals every authenticated route can answer before its handler runs: no usable
    /// credential, and the global or per-policy rate limiter.
    /// </summary>
    /// <remarks>
    /// 429 is declared because it is reachable on EVERY route — the global 100/min limiter sits in
    /// front of all of them — and because a client that does not expect it retries into it.
    /// </remarks>
    public static TBuilder ProducesAuthProblems<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
}
