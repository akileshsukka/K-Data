using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace PresApp.Api.Auth;

/// <summary>
/// Development only: signs in as Auth:DevelopmentUserEmail (or the X-Dev-User header) so the app
/// can be used without Cloudflare. Never registered outside the Development environment.
/// </summary>
public sealed class DevelopmentAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IOptions<PresAppAuthOptions> authOptions,
    UserDirectory directory)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var email = Request.Headers["X-Dev-User"].FirstOrDefault() ?? authOptions.Value.DevelopmentUserEmail;
        if (string.IsNullOrWhiteSpace(email)) return AuthenticateResult.NoResult();
        var user = await directory.FindActiveAsync(email, Context.RequestAborted);
        return user is null
            ? AuthenticateResult.Fail("NotRegistered")
            : AuthenticateResult.Success(new AuthenticationTicket(UserDirectory.ToPrincipal(user, Scheme.Name), Scheme.Name));
    }
}
