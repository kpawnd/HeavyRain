using HeavyRain.Core;

namespace HeavyRain;

/// <summary>
/// One row of the Anti-Executable activity list. A class rather than a record, so two identical
/// log lines written in the same second stay separate rows for selection.
/// </summary>
public sealed class AecActivityItem(AeLogEntry entry)
{
    public AeLogEntry Entry { get; } = entry;
    public string Time => Entry.Time;
    public string Title => Entry.Event;
    public string Subject => Entry.FileName is { Length: > 0 } name ? "  ·  " + name : "";
    public bool HasFile => Entry.FilePath is not null;

    /// <summary>Product and version, publisher, account, and the folder, as far as the log recorded them.</summary>
    public string Detail => string.Join("  ·  ", DetailParts(includePath: true));

    public string FullDetail => string.Join("\n", new[]
    {
        $"{Entry.Time}  {Entry.Event}",
        Entry.FilePath,
        Entry.Product is null ? null : $"Product: {Product()}",
        Entry.Publisher is null ? null : $"Publisher: {Entry.Publisher}",
        Entry.User is null ? null : $"Account: {Entry.User}",
        Entry.Hash is null ? null : $"Hash: {Entry.Hash}",
        Entry.FilePath is null && Entry.Message != Entry.Event ? Entry.Message : null
    }.Where(line => line is not null));

    public string Summary => $"{Time}. {Title}{Subject}. {Detail}";

    private IEnumerable<string> DetailParts(bool includePath)
    {
        if (Entry.Product is not null) yield return Product();
        if (Entry.Publisher is not null) yield return Entry.Publisher;
        if (Entry.User is not null) yield return Entry.User;
        if (includePath && Entry.FilePath is { } path && path.LastIndexOf('\\') is > 0 and var cut) yield return path[..cut];
        if (Entry.FilePath is null && Entry.Message.Length > 0 && Entry.Message != Entry.Event) yield return Entry.Message;
    }

    private string Product() => Entry.Version is null ? Entry.Product! : $"{Entry.Product} {Entry.Version}";
}
