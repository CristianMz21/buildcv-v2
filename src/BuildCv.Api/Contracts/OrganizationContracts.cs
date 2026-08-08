namespace BuildCv.Api.Contracts;

using BuildCv.Domain.Organizations;

public sealed record CreateOrganizationRequest(string Name, string Slug);

public sealed record AddMemberRequest(Guid AccountId, string Role);

// The wire shape of an organization. Like the resume routes, all five /v1/organizations endpoints
// answered with the Domain aggregate, so `id` and `members[].accountId` shipped as {"value": guid},
// `name` and `slug` as {"value": string}, and BOTH enums — OrganizationStatus and MembershipRole —
// as raw integers. `role` is the one that would have bitten first: POST /v1/organizations/{id}/members
// ACCEPTS the name ("Admin") and the read side answered 1, so a client could not round-trip a
// membership through the API without a translation table this contract never published.
public sealed record OrganizationResponse(
    Guid Id,
    string Name,
    string Slug,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<MembershipResponse> Members)
{
    public static OrganizationResponse From(Organization organization)
    {
        ArgumentNullException.ThrowIfNull(organization);

        return new OrganizationResponse(
            organization.Id.Value,
            organization.Name.Value,
            organization.Slug.Value,
            organization.Status.ToString(),
            organization.CreatedAt,
            organization.UpdatedAt,
            [.. organization.Members.Select(MembershipResponse.From)]);
    }
}

public sealed record MembershipResponse(Guid AccountId, string Role, DateTimeOffset JoinedAt)
{
    public static MembershipResponse From(Membership membership) =>
        new(membership.AccountId.Value, membership.Role.ToString(), membership.JoinedAt);
}
