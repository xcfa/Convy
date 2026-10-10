using Convy.PathExpressions.Mappings;
using Convy.PathExpressions.Parsing;
using Microsoft.Extensions.Logging;

namespace Convy.Services.Rules
{
    /// <summary>
    /// File-backed <see cref="IRulesProvider"/> that reloads when the file's last-write
    /// time changes. Polling (rather than <c>FileSystemWatcher</c>) is deliberate:
    /// inotify events do not propagate reliably across Docker bind mounts.
    ///
    /// A failed parse keeps the previously loaded rules and does not bump the version, so
    /// a bad edit never takes the service down or triggers a spurious rescan.
    /// </summary>
    public sealed class RulesProvider : IRulesProvider, IRulesDiagnostics
    {
        private readonly string _path;
        private readonly ILogger<RulesProvider> _logger;
        private readonly object _gate = new();

        private RulesSnapshot _current = new(ConvyMappings.Empty, 0);
        private DateTime? _loadedWriteTimeUtc;
        private string? _lastError;
        private DateTimeOffset? _lastErrorAt;

        public RulesProvider(string path, ILogger<RulesProvider> logger)
        {
            _path = path;
            _logger = logger;
        }

        public string Path => _path;

        public string? LastError
        {
            get
            {
                lock (_gate) return _lastError;
            }
        }

        public DateTimeOffset? LastErrorAt
        {
            get
            {
                lock (_gate) return _lastErrorAt;
            }
        }

        public RulesSnapshot GetCurrent()
        {
            lock (_gate)
            {
                if (!File.Exists(_path))
                {
                    if (_loadedWriteTimeUtc is null && _current.Version == 0)
                        _logger.LogWarning("Rules file '{Path}' not found; no routing rules loaded.", _path);
                    return _current;
                }

                var writeTimeUtc = File.GetLastWriteTimeUtc(_path);
                if (_loadedWriteTimeUtc == writeTimeUtc)
                    return _current;

                // Remember the timestamp even if parsing fails, so we don't re-parse the
                // same broken file every cycle; a later edit changes mtime and retries.
                _loadedWriteTimeUtc = writeTimeUtc;

                try
                {
                    var mappings = ConvyMappings.LoadFromFile(_path);
                    _current = new RulesSnapshot(mappings, _current.Version + 1);
                    _lastError = null;
                    _lastErrorAt = null;
                    _logger.LogInformation(
                        "Loaded {Count} routing rule(s) from '{Path}' (version {Version}).",
                        mappings.Rules.Count, _path, _current.Version);
                }
                catch (FilterParseException ex)
                {
                    _lastError = ex.Message;
                    _lastErrorAt = DateTimeOffset.UtcNow;
                    _logger.LogError(ex,
                        "Failed to parse rules file '{Path}'; keeping previous rules (version {Version}).",
                        _path, _current.Version);
                }

                return _current;
            }
        }
    }

    /// <summary>Where the rules come from and whether the file could be read, for the web UI.</summary>
    public interface IRulesDiagnostics
    {
        /// <summary>Path of the rules file.</summary>
        string Path { get; }

        /// <summary>Why the current file could not be loaded (the previous rules stay in effect), or <c>null</c>.</summary>
        string? LastError { get; }

        /// <summary>When <see cref="LastError"/> was found.</summary>
        DateTimeOffset? LastErrorAt { get; }
    }
}
