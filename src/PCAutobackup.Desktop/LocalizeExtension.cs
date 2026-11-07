using System.Windows;
using System.Windows.Markup;

namespace PCAutobackup.Desktop;

/// <summary>Uses dynamic resources so labels, templates and accessibility names update without recreating controls.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocalizeExtension(string text) : MarkupExtension
{
    public static string ResourceKey(string text) => "Localized:" + text;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new DynamicResourceExtension(ResourceKey(text)).ProvideValue(serviceProvider);
}
