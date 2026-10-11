using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;

namespace Convy.Ui;

/// <summary>
/// Keeps the sign-in state (return address, PKCE verifier, correlation id) in memory and puts
/// only a random key into the OIDC <c>state</c> parameter. The default format puts the whole
/// encrypted state there (~1.5 KB), which ends up in every URL of the sign-in round trip; a
/// reverse proxy that checks <c>/signin-oidc</c> with forward auth copies that URL into its
/// request to the auth server, and together with the cookies it can exceed the server's buffer.
/// </summary>
/// <remarks>
/// A key is 256 random bits and works once; it expires with the sign-in attempt. State kept in
/// memory is lost on a restart, so a sign-in that was in progress then has to be started again.
/// </remarks>
public sealed class ServerSideStateFormat : ISecureDataFormat<AuthenticationProperties>
{
    private const string Prefix = "convy-oidc-state:";

    private readonly IMemoryCache _cache;
    private readonly TimeSpan _lifetime;

    public ServerSideStateFormat(IMemoryCache cache, TimeSpan lifetime)
    {
        _cache = cache;
        _lifetime = lifetime;
    }

    public string Protect(AuthenticationProperties data) => Protect(data, purpose: null);

    public string Protect(AuthenticationProperties data, string? purpose)
    {
        var key = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        // A copy: the handler may keep changing its instance after the state was written.
        var copy = new AuthenticationProperties(new Dictionary<string, string?>(data.Items, StringComparer.Ordinal));
        _cache.Set(CacheKey(key, purpose), copy, _lifetime);
        return key;
    }

    public AuthenticationProperties? Unprotect(string? protectedText) => Unprotect(protectedText, purpose: null);

    public AuthenticationProperties? Unprotect(string? protectedText, string? purpose)
    {
        if (string.IsNullOrEmpty(protectedText))
        {
            return null;
        }

        var cacheKey = CacheKey(protectedText, purpose);
        if (!_cache.TryGetValue(cacheKey, out AuthenticationProperties? properties))
        {
            return null;
        }

        // One use only: a replayed callback finds nothing.
        _cache.Remove(cacheKey);
        return properties;
    }

    private static string CacheKey(string key, string? purpose) => $"{Prefix}{purpose}:{key}";
}
