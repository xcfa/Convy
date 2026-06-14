using Banned.Qbittorrent.Models.Enums;

namespace Convy.Services
{
    public static class EnumTorrentStateExtensions
    {
        /// <summary>
        /// States that mean the content has finished downloading (i.e. the files
        /// exist on disk and are being seeded / checked / paused on the upload side).
        /// </summary>
        public static IReadOnlyCollection<EnumTorrentState> DownloadedState { get; } =
        [
            EnumTorrentState.CheckingUpload,
            EnumTorrentState.ForcedUpload,
            EnumTorrentState.QueuedUpload,
            EnumTorrentState.StalledUpload,
            EnumTorrentState.Uploading,
            EnumTorrentState.StoppedUpload
        ];

        public static bool IsDownloaded(this EnumTorrentState state) => DownloadedState.Contains(state);
    }
}
