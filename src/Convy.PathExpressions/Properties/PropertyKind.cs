namespace Convy.PathExpressions.Properties;

/// <summary>
/// The logical value category of a torrent property, which decides how it may be
/// compared in the DSL. Durations and timestamps are projected onto
/// <see cref="Numeric"/> (seconds / unix-seconds) so they can be compared with the
/// relational operators; the enum state is exposed as <see cref="Text"/>.
/// </summary>
public enum PropertyKind
{
    Numeric,
    Text,
    Boolean,
    Collection,
}
