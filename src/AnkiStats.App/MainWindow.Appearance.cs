using System.Windows;
using AnkiStats.Core;

namespace AnkiStats.App;

public partial class MainWindow
{
    private AppearanceSettings FontSizes => new((int)SidebarSize.Value, (int)TabSize.Value, (int)ContentSize.Value, (int)TableSize.Value);

    private void SetFontSliders(AppearanceSettings sizes)
    {
        SidebarSize.Value = sizes.Sidebar;
        TabSize.Value = sizes.Tabs;
        ContentSize.Value = sizes.Content;
        TableSize.Value = sizes.Table;
    }

    private void LoadAppearance()
    {
        try { SetFontSliders(store.LoadAppearance()); }
        catch (Exception error) when (error is System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            FontSettingsStatus.Text = "Saved font sizes could not be loaded. Default sizes are in use.";
        }
    }

    private void FontSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready) return;
        Typography.Current.Apply(FontSizes);
        FontSettingsStatus.Text = "";
    }

    private void ResetFontSizes(object sender, RoutedEventArgs e) => SetFontSliders(new());

    private void SaveFontSizes(object sender, RoutedEventArgs e)
    {
        try
        {
            store.SaveAppearance(FontSizes);
            FontSettingsStatus.Text = "Font sizes saved.";
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            FontSettingsStatus.Text = "Font sizes could not be saved. " + error.Message;
        }
    }
}
