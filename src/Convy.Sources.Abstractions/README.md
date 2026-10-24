# Convy.Sources.Abstractions

The contract between [Convy](https://github.com/xcfa/convy) and its source plugins: `IContentSource`,
`DownloadPayload`, `Protocol`, the `Gallery` protocol (`IGallerySource`, `GalleryPayload`) and the plugin entry point
(`SourcePlugin`, `IPluginContext`). A plugin references this package with `ExcludeAssets="runtime"`: the host provides
the assembly. See the repository's README, section "Plugins".
