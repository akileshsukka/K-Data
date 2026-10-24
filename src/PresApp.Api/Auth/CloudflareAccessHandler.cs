using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PresApp.Domain.Security;

namespace PresApp.Api.Auth;

/// <summary>
/// Signs the user in from the token Cloudflare Access adds to every request that passed its login.
/// The token's signature, issuer, audience and expiry are checked; then the e-mail must belong to
/// an active PresApp user. There is no other password.
/// </summary>
public sealed class CloudflareAccessHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IOptions<PresAppAuthOptions> authOptions,
    CloudflareKeyProvider keyProvider,
    UserDirectory directory,
    LoginRecorder recorder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    public const string HeaderName = "Cf-Access-Jwt-Assertion";
    public const string CookieName = "CF_Authorization";
    private static readonly JsonWebTokenHandler TokenHandler = new();

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Headers[HeaderName].FirstOrDefault() ?? Request.Cookies[CookieName];
        if (string.IsNullOrWhiteSpace(token)) return AuthenticateResult.NoResult();

        var settings = authOptions.Value;
        if (string.IsNullOrWhiteSpace(settings.CloudflareTeamDomain) || string.IsNullOrWhiteSpace(settings.CloudflareAudience))
        {
            return AuthenticateResult.Fail("Cloudflare Access is not configured (Auth:CloudflareTeamDomain / Auth:CloudflareAudience).");
        }

        string? kid;
        try
        {
            kid = TokenHandler.ReadJsonWebToken(token).Kid;
        }
        catch (ArgumentException)
        {
            return AuthenticateResult.Fail("Malformed Cloudflare token.");
        }

        var keys = await keyProvider.GetKeysAsync(kid, Context.RequestAborted);
        var result = await TokenHandler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "https://" + settings.CloudflareTeamDomain.Trim().TrimEnd('/'),
            ValidAudience = settings.CloudflareAudience,
            IssuerSigningKeys = keys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        });
        if (!result.IsValid) return AuthenticateResult.Fail("Invalid Cloudflare token: " + result.Exception?.Message);

        var jwt = (JsonWebToken)result.SecurityToken;
        var email = jwt.TryGetPayloadValue<string>("email", out var e) ? e : null;
        if (string.IsNullOrWhiteSpace(email)) return AuthenticateResult.Fail("The Cloudflare token has no e-mail.");

        var ip = Request.Headers["Cf-Connecting-IP"].FirstOrDefault() ?? Context.Connection.RemoteIpAddress?.ToString();
        var sessionKey = email + "|" + jwt.IssuedAt.Ticks;
        var user = await directory.FindActiveAsync(email, Context.RequestAborted);
        if (user is null)
        {
            await recorder.RecordAsync(sessionKey, email, null, false, LoginMethod.Cloudflare, ip,
                "E-mail not registered or inactive", Context.RequestAborted);
            return AuthenticateResult.Fail("NotRegistered");
        }

        await recorder.RecordAsync(sessionKey, user.Email, user.Id, true, LoginMethod.Cloudflare, ip, null, Context.RequestAborted);
        return AuthenticateResult.Success(new AuthenticationTicket(UserDirectory.ToPrincipal(user, Scheme.Name), Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var failure = (await HandleAuthenticateOnceSafeAsync()).Failure?.Message;
        var notRegistered = failure == "NotRegistered";
        await AuthProblems.WriteAsync(Context, 401, notRegistered ? "not_registered" : "no_session",
            notRegistered
                ? "Tu correo no está registrado en PresApp o está inactivo. Pide acceso a un administrador."
                : "Sesión no válida. Vuelve a entrar por el enlace de PresApp.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        AuthProblems.WriteAsync(Context, 403, "forbidden", "No tienes permiso para esta acción.");
}
