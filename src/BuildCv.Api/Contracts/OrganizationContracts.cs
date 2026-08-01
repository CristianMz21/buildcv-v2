namespace BuildCv.Api.Contracts;

public sealed record CreateOrganizationRequest(string Name, string Slug);

public sealed record AddMemberRequest(Guid AccountId, string Role);
