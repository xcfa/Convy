# Convy.Sources.Abstractions

The API of Convy's own source extensions: the sources that are not sites — Soulseek through slskd, the torrent
indexers of Prowlarr, OPDS catalogs. A site (MangaDex, E-Hentai, nhentai) is an extension of the shared
`Shizuka.Sources.Abstractions` alone: Shizuka and Convy both load it, Convy downloads from its sources that have the
`Download` capability.

This API is built on the shared one: a `ContentSource` has a `SourceManifest` (id, name, domains, settings, rate
limit) and every call gets an `ISourceContext` (HTTP on the manifest's domains and the hosts of its `Url` settings,
the settings, the log). A source finds releases (`SearchAsync`) and says how to fetch one (`ResolveAsync`: a
`TorrentRelease`, a `SoulseekRelease` or a `FilesRelease`); Convy's downloaders fetch it. A `ContentSourceProvider`
lists sources a service has (an indexer of Prowlarr each).

Reference this package and `Shizuka.Sources.Abstractions` with `ExcludeAssets="runtime"`: the host provides both.
Only Convy loads such a package; Shizuka refuses it as an extension for another app. Test it with
`Shizuka.Sources.Testing`: `ExtensionTestHost.Load(dll, hostApis: [typeof(ContentSource).Assembly], hostTypes: …)`.
