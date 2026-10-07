using Convy.Infrastructure.Helpers;
using Convy.Services.Downloads;
using Convy.Services.Rules;
using Microsoft.Extensions.Logging;

namespace Convy.Services.Storage;

/// <summary>Result of the last storage layout check, shared with the health check.</summary>
public sealed class StorageLayoutStatus
{
    private volatile Snapshot _current = new([], [], null);

    /// <summary>Problems found by the last check (empty when the layout is fine).</summary>
    public IReadOnlyList<string> Problems => _current.Problems;

    /// <summary>Downloaders whose directories could not be read during the last check.</summary>
    public IReadOnlyList<string> Unchecked => _current.Unchecked;

    /// <summary>When the last check ran, or <c>null</c> before the first one.</summary>
    public DateTimeOffset? CheckedAt => _current.CheckedAt;

    public void Set(IReadOnlyList<string> problems, IReadOnlyList<string> notChecked, DateTimeOffset checkedAt) =>
        _current = new Snapshot(problems, notChecked, checkedAt);

    private sealed record Snapshot(IReadOnlyList<string> Problems, IReadOnlyList<string> Unchecked, DateTimeOffset? CheckedAt);
}

/// <summary>
/// Hard links cannot cross mounts, so every rule path must live on the same mount as the
/// download directories of every downloader. Run whenever the rules are (re)loaded; problems
/// are logged as errors and exposed through <c>/health</c>.
/// </summary>
public sealed class StorageLayoutValidator
{
    private readonly IDownloaderResolver _downloaders;
    private readonly IFileSystemInspector _fileSystem;
    private readonly StorageLayoutStatus _status;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StorageLayoutValidator> _logger;

    public StorageLayoutValidator(
        IDownloaderResolver downloaders,
        IFileSystemInspector fileSystem,
        StorageLayoutStatus status,
        TimeProvider timeProvider,
        ILogger<StorageLayoutValidator> logger)
    {
        _downloaders = downloaders;
        _fileSystem = fileSystem;
        _status = status;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Checks the rule paths against every downloader's directories. Returns <c>false</c> when
    /// a downloader could not be asked for its directories, so the check should be repeated.
    /// </summary>
    public async Task<bool> ValidateAsync(RulesSnapshot rules, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var notChecked = new List<string>();

        var rulePaths = new List<(string Rule, string Path, string? Mount)>();
        foreach (var rule in rules.Mappings.Rules)
        {
            var mount = await _fileSystem.GetMountIdAsync(rule.OutputPath, cancellationToken).ConfigureAwait(false);
            rulePaths.Add((rule.Name ?? rule.RawCondition, rule.OutputPath, mount));
        }

        foreach (var downloader in _downloaders.All)
        {
            IReadOnlyList<string> directories;
            try
            {
                directories = await downloader.GetDownloadDirectoriesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not read the download directories of {Provider}; storage check postponed.", downloader.Provider);
                notChecked.Add(downloader.Provider);
                continue;
            }

            foreach (var directory in directories)
            {
                if (!_fileSystem.DirectoryExists(directory))
                {
                    problems.Add(
                        $"{downloader.Provider} download directory '{directory}' is not visible inside Convy; " +
                        "mount it at the same absolute path.");
                    continue;
                }

                var mount = await _fileSystem.GetMountIdAsync(directory, cancellationToken).ConfigureAwait(false);
                if (mount is null)
                {
                    continue;
                }

                foreach (var (rule, path, ruleMount) in rulePaths)
                {
                    if (ruleMount is not null && ruleMount != mount)
                    {
                        problems.Add(
                            $"Rule '{rule}' path '{path}' is on a different mount than {downloader.Provider} " +
                            $"download directory '{directory}'; hard links will fail (EXDEV).");
                    }
                }
            }
        }

        foreach (var problem in problems)
        {
            _logger.LogError("Storage layout: {Problem}", problem);
        }

        _status.Set(problems, notChecked, _timeProvider.GetUtcNow());
        return notChecked.Count == 0;
    }
}
