namespace BuildCv.Api.Contracts;

public sealed record RegisterRequest(string Email, string Password, string? Role);

public sealed record LoginRequest(string Email, string Password);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record TokenResponse(string AccessToken, int ExpiresIn);

public sealed record AntiforgeryTokenResponse(string RequestToken);
