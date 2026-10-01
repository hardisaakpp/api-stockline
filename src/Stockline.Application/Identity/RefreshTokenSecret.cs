using System.Security.Cryptography;
using System.Text;

namespace Stockline.Application.Identity;

internal static class RefreshTokenSecret
{
    public static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
