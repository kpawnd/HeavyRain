using System.Globalization;
using HeavyRain.Core;

namespace HeavyRain;

/// <summary>One row of the Deep Freeze history list.</summary>
public sealed class DfHistoryItem(DfHistoryEntry entry)
{
    public DfHistoryEntry Entry { get; } = entry;
    public string Time => Entry.Time.LocalDateTime.ToString("ddd d MMM  HH:mm:ss", CultureInfo.InvariantCulture);
    public string Title => Entry.Title;
    public string Detail => Entry.Detail;
    public string FullDetail => $"{Entry.Time.LocalDateTime:yyyy-MM-dd HH:mm:ss}  {Entry.Title}\n{Entry.Detail}";
    public string Summary => $"{Time}. {Title}. {Detail}";

    /// <summary>Picks the stripe colour: Frozen and Thawed boots, requests, failures, and everything else.</summary>
    public string Tone => Entry switch
    {
        { Failed: true } => "Failed",
        { Kind: DfEventKind.BootStatus, State: ProtectionState.Frozen } => "Frozen",
        { Kind: DfEventKind.BootStatus, State: ProtectionState.Thawed } => "Thawed",
        { Kind: DfEventKind.NextBootRequest or DfEventKind.ImmediateChange } => "Request",
        _ => "Other"
    };
}

/// <summary>One row of the components list on the installation tab.</summary>
public sealed class ComponentItem(ComponentStatus status)
{
    public string Name => status.Name;
    public string Role => status.Role;
    public string StateText => status.Installed ? status.StartMode is null ? status.State! : $"{status.State} · {status.StartMode}" : "Not registered";
    public string Tone => status.Running ? "Good" : status.Installed ? "Bad" : "None";
}
