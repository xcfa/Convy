using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Convy.Services.Ui;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Convy.Ui;

/// <summary>
/// Wires the web UI: the page and its assets at <c>/</c>, the API at <c>/api/ui</c>, and sign-in
/// with OpenID Connect (Authelia or any other provider). Only the UI is protected this way;
/// <c>/mcp</c>, <c>/sync</c>, <c>/health</c> and <c>/api/v1</c> keep their own access rules.
/// </summary>
public static class UiSetup
{
    /// <summary>Authorization policy of every UI endpoint.</summary>
    public const string Policy = "ui";

    /// <summary>Header the UI sends with every state-changing request (a cross-site form cannot).</summary>
    public const string RequestHeader = "X-Convy-UI";

    private const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    private const string OidcScheme = OpenIdConnectDefaults.AuthenticationScheme;

    public static void AddConvyUi(this WebApplicationBuilder builder, string? connectionString)
    {
        var options = builder.Configuration.GetSection(UiOptions.SectionName).Get<UiOptions>() ?? new UiOptions();
        var mode = options.ResolveMode(out var problem);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new UiState(mode, problem));
        builder.Services.AddSingleton<UiStatusService>();
        builder.Services.AddSingleton<DataBrowserService>();
        builder.Services.AddSingleton<UiAuthService>();
        builder.Services.AddSingleton<UiAssets>();
        builder.Services.AddSingleton<UiRequestErrorFilter>();

        // Without a usable mode the UI's API does not exist at all.
        builder.Services.Configure<MvcOptions>(mvc => mvc.Conventions.Add(new UiControllersConvention(mode != UiMode.Disabled)));

        builder.Services.AddAuthorization(authorization => authorization.AddPolicy(Policy, policy =>
        {
            switch (mode)
            {
                case UiMode.Oidc:
                    policy.RequireAuthenticatedUser()
                        .RequireAssertion(context => options.Oidc.Allows(context.User.FindAll("groups").Select(c => c.Value)));
                    break;
                case UiMode.Open:
                    policy.RequireAssertion(_ => true);
                    break;
                default:
                    policy.RequireAssertion(_ => false);
                    break;
            }
        }));

        if (mode == UiMode.Oidc)
        {
            AddOidc(builder, options.Oidc, connectionString);
        }
    }

    public static void UseConvyUi(this WebApplication app)
    {
        var state = app.Services.GetRequiredService<UiState>();
        var options = app.Services.GetRequiredService<UiOptions>();

        // Behind a reverse proxy the request arrives as plain HTTP on an internal host; the
        // sign-in callback and the cookies must use the address the browser sees.
        if (options.TryGetPublicHost(out var scheme, out var publicHost))
        {
            var host = new HostString(publicHost);
            app.Use((context, next) =>
            {
                context.Request.Scheme = scheme;
                context.Request.Host = host;
                return next(context);
            });
        }

        switch (state.Mode)
        {
            case UiMode.Oidc:
                app.Logger.LogInformation("Web UI at / with OIDC sign-in ({Authority}).", options.Oidc.Authority);
                if (options.Oidc.ClientSecretLooksLikeDigest)
                {
                    app.Logger.LogWarning(
                        "Ui:Oidc:ClientSecret looks like a password digest ($pbkdf2…, $argon2…). Convy needs the plain " +
                        "secret; the digest belongs into the provider's client registration.");
                }

                if (options.Oidc.ClientSecretHasSurroundingWhitespace)
                {
                    app.Logger.LogInformation(
                        "Ui:Oidc:ClientSecret has spaces or line breaks around it (e.g. a secret file with Windows " +
                        "line ends); they are ignored.");
                }

                break;
            case UiMode.Open:
                app.Logger.LogWarning("Web UI at / is open without sign-in (Ui:Auth = none); only the IP allow-list protects it.");
                break;
            default:
                app.Logger.LogInformation("Web UI is off: {Problem}", state.Problem);
                break;
        }

        // State-changing UI requests must come from the UI's own scripts.
        app.Use((context, next) =>
        {
            if (UiRequestRules.NeedsUiHeader(context.Request.Method, context.Request.Path.Value ?? "/")
                && !context.Request.Headers.ContainsKey(RequestHeader))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return Task.CompletedTask;
            }

            return next(context);
        });

        app.UseAuthentication();
        app.UseAuthorization();
    }

    public static void MapConvyUi(this IEndpointRouteBuilder app)
    {
        var state = app.ServiceProvider.GetRequiredService<UiState>();
        if (state.Mode == UiMode.Disabled)
        {
            return;
        }

        app.MapGet("/", (UiAssets assets) => assets.Index()).RequireAuthorization(Policy).ExcludeFromDescription();
        app.MapGet("/assets/{**path}", (string path, UiAssets assets) => assets.Asset(path)).RequireAuthorization(Policy).ExcludeFromDescription();

        if (state.Mode != UiMode.Oidc)
        {
            return;
        }

        var auth = app.MapGroup("/auth").AllowAnonymous().ExcludeFromDescription();
        auth.MapGet("/login", (UiAuthService service) => service.SignIn());
        auth.MapPost("/logout", (HttpContext context, UiAuthService service) => service.SignOutAsync(context));
        auth.MapGet("/signed-out", (UiAuthService service) => service.Page(UiPage.SignedOut));
        auth.MapGet("/denied", (UiAuthService service) => service.Page(UiPage.Denied));
        auth.MapGet("/failed", (UiAuthService service) => service.Page(UiPage.Failed));
    }

    private static void AddOidc(WebApplicationBuilder builder, UiOidcOptions oidc, string? connectionString)
    {
        // Cookies are encrypted with Data Protection keys; kept next to the database so a
        // restart does not sign everyone out.
        var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Convy");
        if (KeysDirectory(connectionString) is { } keys)
        {
            dataProtection.PersistKeysToFileSystem(keys);
        }

        builder.Services
            .AddAuthentication(authentication =>
            {
                authentication.DefaultScheme = CookieScheme;
                authentication.DefaultChallengeScheme = OidcScheme;
            })
            .AddCookie(CookieScheme, cookie =>
            {
                cookie.Cookie.Name = "convy.auth";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.ExpireTimeSpan = TimeSpan.FromHours(12);
                cookie.SlidingExpiration = true;
                cookie.AccessDeniedPath = "/auth/denied";
                cookie.Events.OnRedirectToAccessDenied = context =>
                {
                    if (IsApi(context.Request))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    }
                    else
                    {
                        context.Response.Redirect(context.RedirectUri);
                    }

                    return Task.CompletedTask;
                };
            })
            .AddOpenIdConnect(OidcScheme, options =>
            {
                options.Authority = oidc.Authority;
                options.ClientId = oidc.ClientId;
                options.ClientSecret = oidc.EffectiveClientSecret;
                options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;

                // Authorization code with PKCE. The response comes back as a top-level GET, so
                // the correlation cookies can be SameSite=Lax and work over plain HTTP as well.
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.UsePkce = true;
                options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.NonceCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

                options.Scope.Clear();
                foreach (var scope in oidc.EffectiveScopes)
                {
                    options.Scope.Add(scope);
                }

                // Authelia 4.39+ puts groups and names only into the userinfo response.
                options.GetClaimsFromUserInfoEndpoint = true;
                options.MapInboundClaims = false;
                options.ClaimActions.MapUniqueJsonKey("preferred_username", "preferred_username");
                options.ClaimActions.MapJsonKey("groups", "groups");
                options.TokenValidationParameters.NameClaimType = "preferred_username";
                options.TokenValidationParameters.RoleClaimType = "groups";
                options.SaveTokens = false;
                options.DisableTelemetry = true;

                options.Events.OnRedirectToIdentityProvider = context =>
                {
                    // Scripts get a status code, not a redirect to the provider's login page.
                    if (IsApi(context.Request))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.HandleResponse();
                    }

                    return Task.CompletedTask;
                };
                options.Events.OnRemoteFailure = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger(typeof(UiSetup));
                    if (UiOidcOptions.ExplainSignInFailure(context.Failure?.Message) is { } hint)
                    {
                        // The handler has already logged the provider's error with its stack trace.
                        logger.LogError("UI sign-in failed: {Hint}", hint);
                    }
                    else
                    {
                        logger.LogWarning(context.Failure, "UI sign-in failed.");
                    }

                    context.Response.Redirect("/auth/failed");
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
            });
    }

    private static bool IsApi(HttpRequest request) => request.Path.StartsWithSegments("/api");

    private static DirectoryInfo? KeysDirectory(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource.StartsWith(':'))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        return directory is null ? null : new DirectoryInfo(Path.Combine(directory, "keys"));
    }
}

/// <summary>The UI mode chosen at startup, and why the UI is off when it is.</summary>
public sealed record UiState(UiMode Mode, string? Problem);
