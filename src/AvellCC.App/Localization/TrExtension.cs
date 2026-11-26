using System.Windows.Data;
using System.Windows.Markup;

namespace AvellCC.App.Localization;

/// <summary>
/// XAML: <c>Text="{l:Tr dash.cpu}"</c>. Binds to <see cref="Loc.Instance"/>'s indexer, so the text follows
/// language changes at runtime.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
