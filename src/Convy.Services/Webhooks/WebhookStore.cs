using System.Text.Json;
using System.Text.Json.Serialization;
using Convy.Data.Context;
using Convy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Convy.Services.Webhooks;

/// <summary>A webhook created in the web UI.</summary>
public sealed record StoredWebhook(int Id, WebhookConfig Config, bool Enabled, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Persistence of the webhooks created in the web UI.</summary>
public interface IWebhookStore
{
    Task<IReadOnlyList<StoredWebhook>> ListAsync(CancellationToken cancellationToken);

    Task<StoredWebhook> AddAsync(WebhookConfig config, bool enabled, CancellationToken cancellationToken);

    /// <summary>Replaces a webhook; <c>null</c> when it does not exist.</summary>
    Task<StoredWebhook?> UpdateAsync(int id, WebhookConfig config, bool enabled, CancellationToken cancellationToken);

    /// <summary>Removes a webhook; <c>false</c> when it does not exist.</summary>
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken);
}

/// <summary>EF Core implementation of <see cref="IWebhookStore"/>.</summary>
public sealed class EfWebhookStore : IWebhookStore
{
    // Places as names ("Body"), so the stored JSON reads well in the database view.
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly IDbContextFactory<ConvyDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public EfWebhookStore(IDbContextFactory<ConvyDbContext> dbFactory, TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<StoredWebhook>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entries = await db.Webhooks.AsNoTracking().OrderBy(w => w.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return entries.Select(ToStored).ToList();
    }

    public async Task<StoredWebhook> AddAsync(WebhookConfig config, bool enabled, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var entry = new WebhookEntry { Url = config.Url, CreatedAt = now };
        Apply(config, enabled, now, entry);
        db.Webhooks.Add(entry);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToStored(entry);
    }

    public async Task<StoredWebhook?> UpdateAsync(int id, WebhookConfig config, bool enabled, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var entry = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == id, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        Apply(config, enabled, _timeProvider.GetUtcNow(), entry);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToStored(entry);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Webhooks.Where(w => w.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static void Apply(WebhookConfig config, bool enabled, DateTimeOffset now, WebhookEntry entry)
    {
        entry.Name = config.Name;
        entry.Url = config.Url;
        entry.EventsJson = config.Events is { Count: > 0 } ? JsonSerializer.Serialize(config.Events, Json) : null;
        entry.NamesJson = config.Names is { Count: > 0 } ? JsonSerializer.Serialize(config.Names, Json) : null;
        entry.ParamsJson = config.Params is { Count: > 0 } ? JsonSerializer.Serialize(config.Params, Json) : null;
        entry.Enabled = enabled;
        entry.UpdatedAt = now;
    }

    private static StoredWebhook ToStored(WebhookEntry entry) => new(
        entry.Id,
        new WebhookConfig
        {
            Name = entry.Name,
            Url = entry.Url,
            Events = entry.EventsJson is null ? null : JsonSerializer.Deserialize<List<string>>(entry.EventsJson, Json),
            Names = entry.NamesJson is null ? null : JsonSerializer.Deserialize<List<string>>(entry.NamesJson, Json),
            Params = entry.ParamsJson is null ? null : JsonSerializer.Deserialize<List<WebhookParam>>(entry.ParamsJson, Json),
        },
        entry.Enabled,
        entry.CreatedAt,
        entry.UpdatedAt);
}

/// <summary>
/// All webhooks in effect: those from configuration.yml (read on every call, so edits apply
/// without a restart) followed by the enabled ones created in the web UI (kept in memory and
/// reloaded after every change).
/// </summary>
public sealed class WebhookCatalog
{
    private readonly WebhookConfigSource _fileWebhooks;
    private readonly IWebhookStore _store;
    private readonly ILogger<WebhookCatalog> _logger;

    private volatile IReadOnlyList<StoredWebhook> _stored = [];

    public WebhookCatalog(WebhookConfigSource fileWebhooks, IWebhookStore store, ILogger<WebhookCatalog> logger)
    {
        _fileWebhooks = fileWebhooks;
        _store = store;
        _logger = logger;
    }

    /// <summary>Webhooks from configuration.yml.</summary>
    public IReadOnlyList<WebhookConfig> FileWebhooks => _fileWebhooks.Current;

    /// <summary>Webhooks created in the web UI, enabled or not.</summary>
    public IReadOnlyList<StoredWebhook> StoredWebhooks => _stored;

    /// <summary>Every webhook that is called.</summary>
    public IReadOnlyList<WebhookConfig> Active => [.. FileWebhooks, .. _stored.Where(w => w.Enabled).Select(w => w.Config)];

    /// <summary>Reads the stored webhooks again. A failure keeps the previous ones and is logged.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            _stored = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not load the webhooks created in the UI; keeping {Count} known.", _stored.Count);
        }
    }
}
