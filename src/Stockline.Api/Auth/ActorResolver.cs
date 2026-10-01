using System.Security.Claims;
using Stockline.Domain.Orders;

namespace Stockline.Api.Auth;

public static class ActorResolver
{
    public static ActorRole? Resolve(ClaimsPrincipal user)
    {
        foreach (var claim in user.FindAll("role"))
        {
            if (Enum.TryParse<ActorRole>(claim.Value, ignoreCase: true, out var role) && Enum.IsDefined(role))
            {
                return role;
            }
        }

        return null;
    }
}
