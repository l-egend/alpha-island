using L = AlphaIsland.PluginSdk.Localization.UiText;
using AlphaIsland.EarbudsV2.Communication.Abstractions;
using AlphaIsland.EarbudsV2.Control.Abstractions;
using AlphaIsland.EarbudsV2.Assets.Vivo;
using AlphaIsland.EarbudsV2.Control.Brands.Vivo;
using AlphaIsland.EarbudsV2.Control.Brands.Huawei;
using AlphaIsland.EarbudsV2.Control.Brands.Xiaomi;
using AlphaIsland.EarbudsV2.Control.Brands.Edifier;
using AlphaIsland.EarbudsV2.Control.Brands.Apple;

namespace AlphaIsland.Plugins.OppoPods;
internal static class MultiBrandSupport
{
    internal static readonly IBrandManagerFactory[] Factories =
    [new VivoManagerFactory(), new HuaweiManagerFactory(), new XiaomiManagerFactory(), new EdifierManagerFactory(), new AppleManagerFactory()];
    internal static IBrandManagerFactory? Find(string name, IReadOnlyCollection<Guid>? services = null)
        => Factories.FirstOrDefault(f => IsName(f, name))
            ?? Factories.FirstOrDefault(f => f.ServiceId != Guid.Empty && f.ProbeEvidence == BrandProbeEvidence.DedicatedService
                && services?.Contains(f.ServiceId) == true);
    private static bool IsName(IBrandManagerFactory factory, string name)
    {
        if (!factory.IsCandidateName(name)) return false;
        if (factory is not XiaomiManagerFactory) return true;
        return new[] { "buds", "ear", "耳机", "flip", "mi air", "air2" }.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase));
    }
    internal static IReadOnlyList<string> ModelNames()
    {
        var names = VivoDeviceModelData.LoadCatalog().Models.Select(x => x.DisplayName).ToList();
        foreach (var route in Enum.GetValues<HuaweiRoute>().Where(HuaweiModels.IsKnown))
            names.Add(HuaweiModels.GetCapabilities(route).DisplayName);
        return OppoPodsManager.DeviceCapabilities.GetModelNames().Concat(names).Concat(AppleModels.GetModelNames()).Distinct().OrderBy(x => x).ToArray();
    }
    internal static IEarbudSession Create(EarbudDevice device, EarbudOptions options, MultiBrandDiscovery discovery)
        => Find(device.Name, device.Candidate?.ServiceIds) is { } factory
            ? new MultiBrandSession(device, options, factory, discovery.OpenAsync)
            : new ProtocolSession(device, options);
}
