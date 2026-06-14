using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Convy.Services
{
    public class QBitTorrentConnectionSettings
    {
        public static TimeSpan MinSyncInterval { get; } = TimeSpan.FromSeconds(5);

        [Required]
        public required string Username { get; set; } = null!;

        public string? Password { get; set; }

        [Required]
        public required string Url { get; set; } = null!;

        public TimeSpan SyncInterval { get; set; } = default!;
    }

    public class ValidateQBitTorrentConnectionSettings : IValidateOptions<QBitTorrentConnectionSettings>
    {
        public ValidateOptionsResult Validate(string? name, QBitTorrentConnectionSettings options)
        {
            var errors = new List<string>();

            if (!Uri.TryCreate(options.Url, UriKind.Absolute, out _))
            {
                errors.Add("Url must be a valid absolute URI.");
            }

            if (options.SyncInterval != TimeSpan.Zero
                && options.SyncInterval < QBitTorrentConnectionSettings.MinSyncInterval)
            {
                errors.Add(
                    $"SyncIntervalSeconds must be 0 (disabled) or >= " +
                    $"{QBitTorrentConnectionSettings.MinSyncInterval}. " +
                    $"Got {options.SyncInterval}.");
            }

            return errors.Count > 0
                ? ValidateOptionsResult.Fail(errors)
                : ValidateOptionsResult.Success;
        }
    }
}
