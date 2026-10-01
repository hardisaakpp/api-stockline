namespace Stockline.Application.Identity;

public static class PasswordPolicy
{
    public const int MinLength = 10;

    public static string? Validate(string? password) => password is { Length: >= MinLength }
        ? null
        : $"The password must be at least {MinLength} characters long.";
}
