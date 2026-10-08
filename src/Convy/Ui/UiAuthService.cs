using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Convy.Services.Ui;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;

namespace Convy.Ui;

/// <summary>The signed-in user as the UI shows it.</summary>
public sealed record UiUser(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("groups")] IReadOnlyList<string> Groups,
    [property: JsonPropertyName("auth")] string Auth);

/// <summary>Small pages shown around sign-in, outside the UI itself.</summary>
public enum UiPage
{
    SignedOut,
    Denied,
    Failed,
}

/// <summary>Sign-in, sign-out and the user's identity for the UI.</summary>
public sealed class UiAuthService
{
    private readonly UiOptions _options;
    private readonly UiState _state;

    public UiAuthService(UiOptions options, UiState state)
    {
        _options = options;
        _state = state;
    }

    public UiUser Describe(ClaimsPrincipal user)
    {
        if (_state.Mode != UiMode.Oidc)
        {
            return new UiUser(null, [], "none");
        }

        var name = user.Identity?.Name ?? user.FindFirst("name")?.Value ?? user.FindFirst("sub")?.Value;
        var groups = user.FindAll("groups").Select(c => c.Value).Order(StringComparer.Ordinal).ToList();
        return new UiUser(name, groups, "oidc");
    }

    /// <summary>Starts the sign-in at the provider and comes back to the UI.</summary>
    public IResult SignIn() =>
        Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [OpenIdConnectDefaults.AuthenticationScheme]);

    /// <summary>
    /// Ends Convy's session and tells the UI where to go next. Authelia has no end-session
    /// endpoint, so its session is ended through <see cref="UiOidcOptions.LogoutUrl"/> when set.
    /// </summary>
    public async Task<IResult> SignOutAsync(HttpContext context)
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);

        var origin = new Uri($"{context.Request.Scheme}://{context.Request.Host.ToUriComponent()}");
        return Results.Json(new Dictionary<string, string> { ["redirect"] = _options.SignedOutRedirect(origin) });
    }

    public IResult Page(UiPage page)
    {
        var (title, text) = page switch
        {
            UiPage.SignedOut => ("Signed out", "You have signed out of Convy."),
            UiPage.Denied => ("Access denied", "Your account is not in a group that may use Convy."),
            _ => ("Sign-in failed", "Signing in did not work. The Convy log has the details."),
        };

        var html =
            $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Convy · {{WebUtility.HtmlEncode(title)}}</title>
            <style>
              body { font: 15px/1.5 system-ui, sans-serif; margin: 0; min-height: 100vh; display: grid; place-items: center;
                     background: #f6f7f9; color: #1d2330; }
              main { max-width: 360px; padding: 32px; background: #fff; border: 1px solid #dde1e7; border-radius: 10px; }
              h1 { font-size: 18px; margin: 0 0 8px; }
              a { color: #2f5bd3; }
              @media (prefers-color-scheme: dark) {
                body { background: #14171c; color: #e3e6eb; } main { background: #1c2027; border-color: #2c323c; } a { color: #8fb0ff; }
              }
            </style>
            </head>
            <body>
            <main>
            <h1>{{WebUtility.HtmlEncode(title)}}</h1>
            <p>{{WebUtility.HtmlEncode(text)}}</p>
            <p><a href="/auth/login">Sign in</a></p>
            </main>
            </body>
            </html>
            """;

        var status = page == UiPage.Denied ? StatusCodes.Status403Forbidden : StatusCodes.Status200OK;
        return Results.Content(html, "text/html; charset=utf-8", statusCode: status);
    }
}
