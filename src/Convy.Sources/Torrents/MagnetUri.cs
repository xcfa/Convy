using System.Text;

namespace Convy.Sources.Torrents;

/// <summary>Helpers for magnet URIs and info hashes.</summary>
public static class MagnetUri
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>
    /// Extracts the info hash as qBittorrent identifies the torrent: the v1 hash
    /// (<c>xt=urn:btih:</c>, hex or base32), otherwise the v2 hash (<c>xt=urn:btmh:1220…</c>)
    /// truncated to 20 bytes. Returns lower-case hex, or <c>null</c> when none is present.
    /// </summary>
    public static string? GetInfoHash(string magnet)
    {
        if (!magnet.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? v2 = null;
        foreach (var part in magnet["magnet:?".Length..].Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || !part[..eq].StartsWith("xt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Uri.UnescapeDataString(part[(eq + 1)..]);
            if (value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeInfoHash(value["urn:btih:".Length..]);
            }

            if (value.StartsWith("urn:btmh:1220", StringComparison.OrdinalIgnoreCase) && value.Length >= "urn:btmh:1220".Length + 40)
            {
                v2 = value.Substring("urn:btmh:1220".Length, 40).ToLowerInvariant();
            }
        }

        return v2;
    }

    /// <summary>
    /// Normalises a v1 info hash given as 40 hex characters or 32 base32 characters to
    /// lower-case hex. Returns <c>null</c> for anything else.
    /// </summary>
    public static string? NormalizeInfoHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }

        hash = hash.Trim();
        if (hash.Length == 40 && hash.All(Uri.IsHexDigit))
        {
            return hash.ToLowerInvariant();
        }

        if (hash.Length == 32)
        {
            return FromBase32(hash.ToUpperInvariant());
        }

        return null;
    }

    /// <summary>Builds a minimal magnet URI for a v1 info hash.</summary>
    public static string Create(string infoHash, string? displayName)
    {
        var builder = new StringBuilder("magnet:?xt=urn:btih:").Append(infoHash);
        if (!string.IsNullOrEmpty(displayName))
        {
            builder.Append("&dn=").Append(Uri.EscapeDataString(displayName));
        }

        return builder.ToString();
    }

    private static string? FromBase32(string value)
    {
        var bytes = new byte[20];
        var buffer = 0;
        var bits = 0;
        var index = 0;

        foreach (var c in value)
        {
            var digit = Base32Alphabet.IndexOf(c);
            if (digit < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes[index++] = (byte)(buffer >> bits);
                buffer &= (1 << bits) - 1;
            }
        }

        return index == 20 ? Convert.ToHexStringLower(bytes) : null;
    }
}
