using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using AnkiStats.Core;

namespace AnkiStats.App;

internal sealed class Typography : INotifyPropertyChanged
{
    public static Typography Current { get; } = new();
    public AppearanceSettings Sizes { get; private set; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(AppearanceSettings sizes)
    {
        if (sizes == Sizes) return;
        Sizes = sizes;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Sizes)));
    }

    public static T WithContentSize<T>(T element, double size) where T : FrameworkElement
    {
        BindingOperations.SetBinding(element, TextElement.FontSizeProperty, new ScaledSizeExtension(size).CreateBinding());
        return element;
    }
}

public enum FontArea { Sidebar, Tabs, Content, Table }

[MarkupExtensionReturnType(typeof(double))]
public sealed class ScaledSizeExtension(double size) : MarkupExtension, IValueConverter
{
    public FontArea Area { get; set; } = FontArea.Content;

    internal Binding CreateBinding() => new($"{nameof(Typography.Sizes)}.{Area}")
    {
        Source = Typography.Current, Mode = BindingMode.OneWay, Converter = this, FallbackValue = size
    };

    public override object ProvideValue(IServiceProvider serviceProvider) => CreateBinding().ProvideValue(serviceProvider);
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => size * (int)value / 100;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
