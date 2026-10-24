using Convy.Services.Downloads;

namespace Convy.Services.Downloaders.Gallery;

/// <summary>Hands gallery downloads from <see cref="GalleryDownloader"/> to the background worker.</summary>
public sealed class GalleryDownloadQueue : WorkQueue;
