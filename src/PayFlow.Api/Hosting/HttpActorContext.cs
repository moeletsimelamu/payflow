using System.Net;
using System.Security.Claims;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Api.Hosting;

/// <summary>Resolves the acting principal from the current HTTP request, or a system actor outside a request.</summary>
public sealed class HttpActorContext(IHttpContextAccessor accessor) : IActorContext
{
    public Actor Actor
    {
        get
        {
            var http = accessor.HttpContext;
            if (http is null)
            {
                return Actor.System("background");
            }

            var user = http.User;
            if (user.Identity?.IsAuthenticated != true ||
                !Enum.TryParse<ActorType>(user.FindFirstValue(PayFlowClaimTypes.ActorType), out var type))
            {
                return Actor.Anonymous;
            }

            var id = user.FindFirstValue(PayFlowClaimTypes.PublicId);
            var display = user.FindFirstValue(ClaimTypes.Email) ?? user.Identity.Name ?? id ?? type.ToString();
            return new Actor(type, id, display);
        }
    }

    public IPAddress? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress;

    public string? UserAgent => accessor.HttpContext?.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;
}
