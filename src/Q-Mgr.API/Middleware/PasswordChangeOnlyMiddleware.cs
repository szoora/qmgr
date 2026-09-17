using QMgr.API.Application.Services;

namespace QMgr.Middleware;

/// <summary>
/// A token issued against a temporary password can do exactly two things: change the password and
/// sign out (duty rota plan §12.3, §13.16). This is the one rule that says so — not a check in each
/// controller, which a new controller would forget. It runs straight after authentication, before
/// tenant resolution, module checks or authorization, so nothing downstream ever sees such a token.
/// <para>
/// 401 rather than 403: the token is not "insufficient", it is not a sign-in at all yet, and a client
/// that sees 401 with <c>PASSWORD_CHANGE_REQUIRED</c> knows where to send the person. SignalR
/// negotiation is refused too, so no live channel opens before the password is theirs.
/// </para>
/// </summary>
public class PasswordChangeOnlyMiddleware
{
    private readonly RequestDelegate _next;

    public PasswordChangeOnlyMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true && user.HasClaim(TemporaryPasswords.ChangeOnlyClaim, "true") && !IsAllowed(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "PASSWORD_CHANGE_REQUIRED",
                message = "Set your own password before using the system."
            });
            return;
        }

        await _next(context);
    }

    private static bool IsAllowed(HttpRequest request)
    {
        var path = request.Path.Value?.TrimEnd('/') ?? string.Empty;
        return (HttpMethods.IsPut(request.Method) && path.Equals("/api/v1/profile/password", StringComparison.OrdinalIgnoreCase))
            || (HttpMethods.IsPost(request.Method) && path.Equals("/api/v1/auth/logout", StringComparison.OrdinalIgnoreCase));
    }
}
