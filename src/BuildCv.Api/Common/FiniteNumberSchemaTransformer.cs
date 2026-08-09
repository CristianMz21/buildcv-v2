using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace BuildCv.Api.Common;

/// <summary>
/// Narrows every floating-point property in the published OpenAPI document from
/// <c>["number", "string"]</c> to <c>"number"</c>.
/// </summary>
/// <remarks>
/// <para>
/// .NET describes a <c>double</c> as EITHER a number or a string, because
/// <c>JsonNumberHandling.AllowNamedFloatingPointLiterals</c> would let one arrive as <c>"NaN"</c> or
/// <c>"Infinity"</c>. This API never turns that on, and no value that reaches these fields can be
/// either: every score and weight is a finite 0..1 produced by arithmetic the engines bound, and
/// <c>impact</c> is a difference of two of them.
/// </para>
/// <para>
/// IT MATTERS BECAUSE THE UNION IS INHERITED BY EVERY GENERATED CLIENT. A client built from the
/// unnarrowed document types <c>score</c> as <c>number | string</c>, so every read of a score has to be
/// narrowed by hand at the call site — and the one that is forgotten silently formats a percentage bar
/// from a string. The document is the contract; stating a union this API cannot produce makes every
/// consumer carry a branch that is dead.
/// </para>
/// <para>
/// Scoped to the schema's own type rather than to a list of property names, so a field added later is
/// covered without anyone remembering this file.
/// </para>
/// </remarks>
internal sealed class FiniteNumberSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        var type = context.JsonTypeInfo.Type;
        if (type == typeof(double) || type == typeof(float)
            || type == typeof(double?) || type == typeof(float?))
        {
            // NULLABILITY IS PRESERVED, not overwritten. In OpenAPI 3.1 "may be absent" is the Null
            // FLAG on the same Type, so assigning Number outright would quietly turn every `double?` on
            // the wire into a required number — the opposite mistake to the one being fixed.
            var nullable = schema.Type?.HasFlag(JsonSchemaType.Null) ?? false;
            schema.Type = nullable ? JsonSchemaType.Number | JsonSchemaType.Null : JsonSchemaType.Number;

            // The pattern only exists to validate the string half. With the string gone it describes
            // nothing, and a generator that honours it would emit a regex check against a number.
            schema.Pattern = null;
        }

        return Task.CompletedTask;
    }
}
