using Convy.Infrastructure.Helpers;
using Convy.Services.Linking;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Convy.Services.Tests;

public class FileLinkingServiceTests
{
    private sealed class FakeLinker : IFileLinker
    {
        public HashSet<string> Existing { get; } = new();
        public List<(string Source, string Destination)> Created { get; } = new();
        public HashSet<string> FailOnSource { get; } = new();

        public bool Exists(string path) => Existing.Contains(path);

        public void Link(string source, string destination)
        {
            if (FailOnSource.Contains(source))
                throw new IOException("link failed");

            Created.Add((source, destination));
            Existing.Add(destination);
        }
    }

    private static FileLinkingService Create(FakeLinker linker) =>
        new(linker, NullLogger<FileLinkingService>.Instance);

    private static string Src(string name) => Path.Combine("/data/downloads", name);

    [Fact]
    public void LinksFilesWhoseSourceExists()
    {
        var linker = new FakeLinker();
        linker.Existing.Add(Src("a.mp4"));
        linker.Existing.Add(Src("b.mp4"));

        var outcome = Create(linker).LinkFiles(
            "/data/downloads", "/data/media", ["a.mp4", "b.mp4"]);

        Assert.True(outcome.AllLinked);
        Assert.Equal(0, outcome.MissingSources);
        Assert.Equal(new[] { "a.mp4", "b.mp4" }, outcome.NewlyLinked);
        Assert.Equal(2, linker.Created.Count);
    }

    [Fact]
    public void RecordsPreexistingDestinationWithoutRelinking()
    {
        var linker = new FakeLinker();
        linker.Existing.Add(Src("a.mp4"));
        linker.Existing.Add(Path.Combine("/data/media", "a.mp4")); // dest already on disk

        var outcome = Create(linker).LinkFiles(
            "/data/downloads", "/data/media", ["a.mp4"]);

        Assert.True(outcome.AllLinked);
        Assert.Equal(new[] { "a.mp4" }, outcome.NewlyLinked); // recorded
        Assert.Empty(linker.Created); // but not re-linked (would be EEXIST)
    }

    [Fact]
    public void MissingSourceIsCountedAndNotAllLinked()
    {
        var linker = new FakeLinker();
        linker.Existing.Add(Src("present.mp4"));
        // "absent.mp4" source does not exist

        var outcome = Create(linker).LinkFiles(
            "/data/downloads", "/data/media", ["present.mp4", "absent.mp4"]);

        Assert.False(outcome.AllLinked);
        Assert.Equal(1, outcome.MissingSources);
        Assert.Equal(new[] { "present.mp4" }, outcome.NewlyLinked);
    }

    [Fact]
    public void LinkFailureLeavesTorrentNotFullyLinked()
    {
        var linker = new FakeLinker();
        linker.Existing.Add(Src("a.mp4"));
        linker.FailOnSource.Add(Src("a.mp4")); // e.g. EXDEV

        var outcome = Create(linker).LinkFiles(
            "/data/downloads", "/data/media", ["a.mp4"]);

        Assert.False(outcome.AllLinked);
        Assert.Equal(0, outcome.MissingSources);
        Assert.Empty(outcome.NewlyLinked);
    }
}
