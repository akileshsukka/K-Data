using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace PresApp.Api.Auth;

public static class AuthSetup
{
    public static IServiceCollection AddPresAppAuth(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        services.Configure<PresAppAuthOptions>(config.GetSection(PresAppAuthOptions.Section));
        services.AddMemoryCache();
        services.AddSingleton<UserDirectory>();
        services.AddSingleton<LoginRecorder>();
        services.AddHttpClient(nameof(CloudflareKeyProvider), c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton(sp => new CloudflareKeyProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(CloudflareKeyProvider)),
            sp.GetRequiredService<IOptions<PresAppAuthOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<CloudflareKeyProvider>>()));

        var useDevelopment = env.IsDevelopment()
                             && !string.IsNullOrWhiteSpace(config[$"{PresAppAuthOptions.Section}:{nameof(PresAppAuthOptions.DevelopmentUserEmail)}"]);

        var auth = services.AddAuthentication(AuthConstants.Scheme)
            .AddPolicyScheme(AuthConstants.Scheme, "PresApp", o =>
            {
                o.ForwardDefaultSelector = context =>
                {
                    var options = context.RequestServices.GetRequiredService<IOptions<PresAppAuthOptions>>().Value;
                    if (EmergencyAccess.IsEmergencyRequest(context, options)) return AuthConstants.EmergencyScheme;
                    return useDevelopment ? AuthConstants.DevelopmentScheme : AuthConstants.CloudflareScheme;
                };
            })
            .AddScheme<AuthenticationSchemeOptions, CloudflareAccessHandler>(AuthConstants.CloudflareScheme, null)
            .AddCookie(AuthConstants.EmergencyScheme, o =>
            {
                o.Cookie.Name = "PresApp.Emergency";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.SlidingExpiration = false;
                o.Events = new CookieAuthenticationEvents
                {
                    // The cookie is honoured only on the local emergency port.
                    OnValidatePrincipal = ctx =>
                    {
                        var options = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<PresAppAuthOptions>>().Value;
                        if (!EmergencyAccess.IsEmergencyRequest(ctx.HttpContext, options)) ctx.RejectPrincipal();
                        return Task.CompletedTask;
                    },
                    OnRedirectToLogin = ctx => AuthProblems.WriteAsync(ctx.HttpContext, 401, "emergency_login", "Inicia sesión de emergencia."),
                    OnRedirectToAccessDenied = ctx => AuthProblems.WriteAsync(ctx.HttpContext, 403, "forbidden", "No tienes permiso para esta acción.")
                };
            });

        if (useDevelopment)
        {
            auth.AddScheme<AuthenticationSchemeOptions, DevelopmentAuthHandler>(AuthConstants.DevelopmentScheme, null);
        }

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddAuthorization();
        return services;
    }
}

/// <summary>Turns policy names like "perm:assets.edit" into a requirement on the permission claim.</summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(AuthConstants.PolicyPrefix, StringComparison.Ordinal))
        {
            return await base.GetPolicyAsync(policyName);
        }
        var permission = policyName[AuthConstants.PolicyPrefix.Length..];
        return new AuthorizationPolicyBuilder(AuthConstants.Scheme)
            .RequireAuthenticatedUser()
            .RequireClaim(AuthConstants.PermissionClaim, permission)
            .Build();
    }
}

public static class EndpointAuthExtensions
{
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(AuthConstants.Policy(permission));
}
