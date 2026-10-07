using Convy.Sources;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Convy.Services.Media;

/// <summary>
/// Renders a file list so it never floods the agent's context, however many files a
/// result has: a summarised top level, one directory level at a time, or a glob match,
/// always paged.
/// </summary>
public static class FileTree
{
    /// <param name="resultId">The result the files belong to.</param>
    /// <param name="files">Every file, relative to the result root.</param>
    /// <param name="path">Directory to expand by one level; <c>null</c> for the top level.</param>
    /// <param name="glob">Pattern returning a flat list of matching files instead of a tree.</param>
    /// <param name="offset">Entries to skip.</param>
    /// <param name="maxEntries">Entries per page.</param>
    public static ListFilesResponse Render(
        string resultId, IReadOnlyList<ListedFile> files, string? path, string? glob, int offset, int maxEntries)
    {
        var directory = NormalizeDirectory(path);
        var scope = directory is null
            ? files
            : files.Where(f => f.Path.StartsWith(directory, StringComparison.Ordinal)).ToList();

        if (directory is not null && scope.Count == 0)
        {
            throw new ConvyRequestException($"There is no directory '{path}' in this result.");
        }

        IReadOnlyList<FileEntryDto> entries;
        if (!string.IsNullOrWhiteSpace(glob))
        {
            var matcher = FileSelector.CreateMatcher(glob);
            entries = scope
                .Where(f => matcher.Match(f.Path).HasMatches)
                .Select(f => new FileEntryDto(f.Path, f.Size))
                .ToList();
        }
        else
        {
            entries = Level(scope, directory ?? string.Empty);
        }

        offset = Math.Max(0, offset);
        maxEntries = Math.Max(1, maxEntries);
        var page = entries.Skip(offset).Take(maxEntries).ToList();
        var hasMore = offset + page.Count < entries.Count;

        return new ListFilesResponse(
            "ok",
            resultId,
            directory,
            page,
            page.Count,
            entries.Count,
            offset,
            hasMore,
            hasMore ? $"Showing {page.Count} of {entries.Count}; call again with offset {offset + page.Count} for more." : null);
    }

    /// <summary>
    /// The direct children of <paramref name="prefix"/>: directories with a summary (file
    /// count, size, extensions) first, then files.
    /// </summary>
    private static List<FileEntryDto> Level(IReadOnlyList<ListedFile> files, string prefix)
    {
        var directories = new SortedDictionary<string, List<ListedFile>>(StringComparer.Ordinal);
        var plain = new List<FileEntryDto>();

        foreach (var file in files)
        {
            var rest = file.Path[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                plain.Add(new FileEntryDto(file.Path, file.Size));
                continue;
            }

            var dir = prefix + rest[..(slash + 1)];
            if (!directories.TryGetValue(dir, out var content))
            {
                directories[dir] = content = [];
            }

            content.Add(file);
        }

        return directories
            .Select(d => new FileEntryDto(d.Key, d.Value.Sum(f => f.Size), d.Value.Count, Extensions(d.Value)))
            .Concat(plain.OrderBy(p => p.Path, StringComparer.Ordinal))
            .ToList();
    }

    private static Dictionary<string, int> Extensions(IEnumerable<ListedFile> files) =>
        files
            .GroupBy(f => System.IO.Path.GetExtension(f.Path).TrimStart('.').ToLowerInvariant() is { Length: > 0 } ext ? ext : "(none)")
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());

    private static string? NormalizeDirectory(string? path)
    {
        var trimmed = path?.Trim().Trim('/');
        return string.IsNullOrEmpty(trimmed) ? null : trimmed + "/";
    }
}

/// <summary>Applies the agent's <c>include</c>/<c>exclude</c> glob patterns to a file list.</summary>
public static class FileSelector
{
    /// <summary>
    /// Files selected by the patterns: everything (or what <paramref name="include"/> matches)
    /// minus what <paramref name="exclude"/> matches. Patterns are globs relative to the
    /// result root (<c>*</c>, <c>**</c>, exact paths). A pattern that matches nothing, or an
    /// empty selection, is an error rather than an empty job.
    /// </summary>
    public static IReadOnlyList<ListedFile> Select(
        IReadOnlyList<ListedFile> files, IReadOnlyList<string>? include, IReadOnlyList<string>? exclude)
    {
        IEnumerable<ListedFile> selected = files;

        if (include is { Count: > 0 })
        {
            var included = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pattern in include)
            {
                var matches = Matching(files, pattern, "include");
                included.UnionWith(matches.Select(f => f.Path));
            }

            selected = files.Where(f => included.Contains(f.Path));
        }

        var result = selected.ToList();

        if (exclude is { Count: > 0 })
        {
            foreach (var pattern in exclude)
            {
                var excluded = Matching(files, pattern, "exclude").Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
                result.RemoveAll(f => excluded.Contains(f.Path));
            }
        }

        return result.Count > 0
            ? result
            : throw new ConvyRequestException("The include/exclude patterns select no file.");
    }

    /// <summary>A case-insensitive matcher for one pattern relative to the result root.</summary>
    public static Matcher CreateMatcher(string pattern)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(pattern.Trim().TrimStart('/'));
        return matcher;
    }

    private static List<ListedFile> Matching(IReadOnlyList<ListedFile> files, string pattern, string kind)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ConvyRequestException($"An {kind} pattern is empty.");
        }

        var matcher = CreateMatcher(pattern);
        var matches = files.Where(f => matcher.Match(f.Path).HasMatches).ToList();

        return matches.Count > 0
            ? matches
            : throw new ConvyRequestException($"The {kind} pattern '{pattern}' matches no file of this result.");
    }
}
