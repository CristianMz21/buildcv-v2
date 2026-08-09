using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace BuildCv.Api.Common;

/// <summary>
/// Narrows every numeric property in the published OpenAPI document from <c>["number", "string"]</c> —
/// or <c>["integer", "string"]</c> — down to the numeric type alone.
/// </summary>
/// <remarks>
/// <para>
/// .NET describes every number as EITHER a number or a string, because two opt-ins would allow the
/// string form: <c>JsonNumberHandling.AllowNamedFloatingPointLiterals</c> would let a double arrive as
/// <c>"NaN"</c>, and <c>WriteAsString</c> would quote integers. This API turns on neither, and no value
/// that reaches these fields could use them anyway — every score and weight is a finite 0..1 produced
/// by arithmetic the engines bound, and every entry id is a surrogate key.
/// </para>
/// <para>
/// IT MATTERS BECAUSE THE UNION IS INHERITED BY EVERY GENERATED CLIENT. Unnarrowed, <c>score</c> types
/// as <c>number | string</c> and so does the <c>id</c> that <c>DELETE
/// /v1/resumes/{id}/{section}/{itemId}</c> takes — so every read of either has to be narrowed by hand
/// at the call site, and the one that is forgotten formats a percentage bar from a string or builds a
/// URL from one. The document is the contract; stating a union this API cannot produce makes every
/// consumer carry a branch that is dead.
/// </para>
/// <para>
/// Scoped to the schema's own CLR type rather than to a list of property names, so a field added later
/// is covered without anyone remembering this file.
/// </para>
/// </remarks>
internal sealed class NumericSchemaTransformer : IOpenApiSchemaTransformer
{
    // Every numeric primitive this API can put on the wire, and its nullable form. decimal is here for
    // completeness rather than because anything uses one today — the point is that the next field to
    // arrive is covered by construction.
    private static readonly HashSet<Type> Fractional =
    [
        typeof(double), typeof(double?),
        typeof(float), typeof(float?),
        typeof(decimal), typeof(decimal?)
    ];

    private static readonly HashSet<Type> Integral =
    [
        typeof(int), typeof(int?),
        typeof(long), typeof(long?),
        typeof(short), typeof(short?),
        typeof(byte), typeof(byte?)
    ];

    public Task TransformAsync(
        OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        var type = context.JsonTypeInfo.Type;

        JsonSchemaType? narrowed =
            Fractional.Contains(type) ? JsonSchemaType.Number
            : Integral.Contains(type) ? JsonSchemaType.Integer
            : null;

        if (narrowed is null)
            return Task.CompletedTask;

        // NULLABILITY IS PRESERVED, not overwritten. In OpenAPI 3.1 "may be absent" is the Null FLAG on
        // the same Type, so assigning the numeric type outright would quietly turn every `int?` on the
        // wire into a required number — the opposite mistake to the one being fixed.
        var nullable = schema.Type?.HasFlag(JsonSchemaType.Null) ?? false;
        schema.Type = nullable ? narrowed.Value | JsonSchemaType.Null : narrowed.Value;

        // The pattern only exists to validate the string half. With the string gone it describes
        // nothing, and a generator that honours it would emit a regex check against a number.
        schema.Pattern = null;

        return Task.CompletedTask;
    }
}
