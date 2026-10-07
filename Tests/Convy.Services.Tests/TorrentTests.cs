using System.Security.Cryptography;
using BencodeNET.Objects;
using BencodeNET.Parsing;
using Convy.Sources.Torrents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

public class MagnetUriTests
{
    [Fact]
    public void ReadsHexInfoHash() =>
        Assert.Equal("0123456789abcdef0123456789abcdef01234567",
            MagnetUri.GetInfoHash("magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567&dn=x"));

    [Fact]
    public void ConvertsBase32InfoHashToHex()
    {
        // 20 zero bytes in base32.
        Assert.Equal(new string('0', 40), MagnetUri.GetInfoHash("magnet:?xt=urn:btih:" + new string('A', 32)));
    }

    [Fact]
    public void FallsBackToTruncatedV2Hash()
    {
        var v2 = new string('f', 64);
        Assert.Equal(new string('f', 40), MagnetUri.GetInfoHash($"magnet:?xt=urn:btmh:1220{v2}"));
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("magnet:?dn=only-a-name")]
    public void ReturnsNullWithoutHash(string uri) => Assert.Null(MagnetUri.GetInfoHash(uri));

    [Fact]
    public void CreatesMagnetFromHash() =>
        Assert.Equal("magnet:?xt=urn:btih:abc&dn=My%20Show", MagnetUri.Create("abc", "My Show"));
}

public class TorrentMetadataTests
{
    /// <summary>Builds a minimal .torrent file.</summary>
    internal static byte[] Torrent(string name, params (string Path, long Size)[] files)
    {
        var info = new BDictionary
        {
            ["name"] = new BString(name),
            ["piece length"] = new BNumber(16384),
            ["pieces"] = new BString(new byte[20]),
        };

        if (files.Length == 1 && !files[0].Path.Contains('/'))
        {
            info["length"] = new BNumber(files[0].Size);
        }
        else
        {
            info["files"] = new BList(files.Select(f => new BDictionary
            {
                ["length"] = new BNumber(f.Size),
                ["path"] = new BList(f.Path.Split('/').Select(p => new BString(p))),
            }));
        }

        return new BDictionary { ["announce"] = new BString("http://tracker/announce"), ["info"] = info }.EncodeAsBytes();
    }

    private static readonly TorrentMetadataService Service = new(new TorrentMetadataOptions(), NullLogger<TorrentMetadataService>.Instance);

    [Fact]
    public void ParsesMultiFileTorrentRelativeToItsRootAndSkipsPadding()
    {
        var bytes = Torrent("Show (2019)", ("Season 01/e1.mkv", 100), (".pad/16384", 5), ("Season 01/e1.srt", 2));

        var metadata = Service.Parse(bytes);

        Assert.Equal("Show (2019)", metadata.Name);
        Assert.Equal(["Season 01/e1.mkv", "Season 01/e1.srt"], metadata.Files.Select(f => f.Path));
        Assert.Equal(100, metadata.Files[0].Size);
        Assert.Same(bytes, metadata.TorrentFile);
    }

    [Fact]
    public void ParsesSingleFileTorrent()
    {
        var metadata = Service.Parse(Torrent("movie.mkv", ("movie.mkv", 42)));

        Assert.Equal("movie.mkv", Assert.Single(metadata.Files).Path);
    }

    [Fact]
    public void InfoHashIsTheSha1OfTheInfoDictionary()
    {
        var bytes = Torrent("x", ("a.bin", 1));
        var info = new BencodeParser().Parse<BDictionary>(bytes)["info"].EncodeAsBytes();

        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(info)), Service.Parse(bytes).InfoHash);
    }

    [Fact]
    public void RejectsGarbage() =>
        Assert.Throws<InvalidDataException>(() => Service.Parse("<html>login</html>"u8.ToArray()));
}
