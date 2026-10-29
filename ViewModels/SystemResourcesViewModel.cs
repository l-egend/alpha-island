using L = AlphaIsland.PluginSdk.Localization.UiText;
using CommunityToolkit.Mvvm.ComponentModel;
using alpha_island.Core;

namespace alpha_island.ViewModels;

/// <summary>One observable resource state for the card, pill and satellite views.</summary>
internal sealed class SystemResourcesViewModel : ObservableObject
{
    public SystemResourceSnapshot Snapshot { get; private set; }
    public string CpuText => Snapshot.CpuPercent is double value ? $"{value:0}%" : "—";
    public string MemoryText => Snapshot.MemoryPercent is double value ? $"{value:0}%" : "—";
    public string MemoryDetail => Snapshot.MemoryDetail ?? L.T("等待内存采样");
    public string PowerText => Snapshot.TotalWatts is double value ? $"{value:0.0} W" : "— W";
    public string PowerDetail => Snapshot.CpuWatts.HasValue || Snapshot.GpuWatts.HasValue
        ? $"CPU {(Snapshot.CpuWatts is double cpu ? $"{cpu:0.0} W" : "—")} · GPU {(Snapshot.GpuWatts is double gpu ? $"{gpu:0.0} W" : "—")}"
        : L.T("等待可用功耗传感器");
    public string? PowerDescription => Snapshot.PowerDetail;

    internal void Update(SystemResourceSnapshot value)
    {
        if (value == Snapshot) return;
        var old = Snapshot;
        Snapshot = value;
        Changed(nameof(Snapshot));
        if (old.CpuPercent != value.CpuPercent) Changed(nameof(CpuText));
        if (old.MemoryPercent != value.MemoryPercent) Changed(nameof(MemoryText));
        if (old.MemoryDetail != value.MemoryDetail) Changed(nameof(MemoryDetail));
        if (old.TotalWatts != value.TotalWatts) Changed(nameof(PowerText));
        if (old.CpuWatts != value.CpuWatts || old.GpuWatts != value.GpuWatts) Changed(nameof(PowerDetail));
        if (old.PowerDetail != value.PowerDetail) Changed(nameof(PowerDescription));
    }
    private void Changed(string name) => OnPropertyChanged(name);
}
