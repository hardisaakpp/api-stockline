using System.Security.Cryptography;
using Stockline.Application.Identity;

namespace Stockline.Infrastructure.Identity;

/// <summary>PBKDF2-SHA256 with a per-password salt. Format: <c>v1.{iterations}.{saltBase64}.{hashBase64}</c>.</summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const string Version = "v1";
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int DefaultIterations = 210_000;

    private readonly int _iterations;

    public Pbkdf2PasswordHasher(int iterations = DefaultIterations)
    {
        _iterations = iterations;
        DummyHash = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));
    }

    public string DummyHash { get; }

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, _iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Version}.{_iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string hash)
    {
        var parts = hash.Split('.');
        if (parts is not [Version, var iterationsText, var saltText, var hashText]
            || !int.TryParse(iterationsText, out var iterations)
            || iterations < 1)
        {
            return false;
        }

        try
        {
            var expected = Convert.FromBase64String(hashText);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password, Convert.FromBase64String(saltText), iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
