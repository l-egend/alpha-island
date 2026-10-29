using System.Windows.Markup;

namespace AlphaIsland.PluginSdk.Localization;

[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => UiText.ByKey(Key);
}
