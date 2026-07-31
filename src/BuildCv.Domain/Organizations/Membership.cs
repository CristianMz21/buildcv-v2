using BuildCv.Domain.Identity;

namespace BuildCv.Domain.Organizations;

public sealed record Membership(
    AccountId AccountId,
    MembershipRole Role,
    DateTimeOffset JoinedAt);
