using System;
using System.ComponentModel.DataAnnotations;

namespace Convy.Data.Entities
{
    /// <summary>
    /// A webhook created in the web UI. Webhooks from configuration.yml are not stored here;
    /// both kinds are used together.
    /// </summary>
    public class WebhookEntry
    {
        public int Id { get; set; }

        [MaxLength(256)]
        public string? Name { get; set; }

        [MaxLength(2048)]
        public required string Url { get; set; }

        /// <summary>JSON array of event names; <c>null</c> for the default (<c>linked</c> only).</summary>
        public string? EventsJson { get; set; }

        /// <summary>JSON array of rule names the webhook is limited to; <c>null</c> for every rule.</summary>
        public string? NamesJson { get; set; }

        /// <summary>JSON array of <c>{ place, name, value }</c>; <c>null</c> to send whole bodies.</summary>
        public string? ParamsJson { get; set; }

        /// <summary>A disabled webhook is kept but never called.</summary>
        public bool Enabled { get; set; } = true;

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
