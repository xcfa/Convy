using Convy.Services.Downloads;

namespace Convy.Services.Downloaders.Opds;

/// <summary>
/// Hands book downloads from <see cref="OpdsDownloader"/> to the background worker, and lets
/// the downloader stop a running one and read its progress.
/// </summary>
public sealed class OpdsDownloadQueue : WorkQueue;
