using System.IO;
using System.Windows;
using System.Windows.Media;

namespace LineSimulator.App;

/// <summary>Tema chiaro/scuro: sostituisce i pennelli di App.xaml (DynamicResource) e i colori di sistema dei controlli.</summary>
public static class Theme
{
    public static bool IsDark { get; private set; }

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LineSimulator", "theme.txt");

    private static SolidColorBrush B(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

    public static void LoadSaved()
    {
        try { Apply(File.Exists(FilePath) && File.ReadAllText(FilePath).Trim() == "dark"); }
        catch { Apply(false); }
    }

    public static void Toggle()
    {
        Apply(!IsDark);
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, IsDark ? "dark" : "light"); } catch { }
    }

    public static void Apply(bool dark)
    {
        IsDark = dark;
        var r = Application.Current.Resources;
        // (chiaro, scuro)
        void Set(object key, string light, string darkHex) => r[key] = B(dark ? darkHex : light);
        Set("Surface", "#FFFFFF", "#1F2730");
        Set("PanelBg", "#F4F6F8", "#161C23");
        Set("Border", "#E3E6EA", "#38424E");
        Set("HeaderBg", "#26323D", "#0F141A");
        Set("Text", "#1B2430", "#E6EBF0");
        Set("TextMuted", "#5B6673", "#9AA7B4");
        Set("HoverBg", "#E6F0FC", "#2B3A4D");
        Set("SoftBg", "#F7F9FB", "#252E39");
        Set("Overlay", "#E6FFFFFF", "#E61F2730");
        Set("Accent", "#0B5FC0", dark ? "#4C9AFF" : "#0B5FC0");
        // colori di sistema usati dai controlli standard (TextBox, ComboBox, DataGrid, Menu, ListView...)
        Set(SystemColors.WindowBrushKey, "#FFFFFF", "#1F2730");
        Set(SystemColors.WindowTextBrushKey, "#1B2430", "#E6EBF0");
        Set(SystemColors.ControlBrushKey, "#F0F0F0", "#252E39");
        Set(SystemColors.ControlTextBrushKey, "#1B2430", "#E6EBF0");
        Set(SystemColors.ControlLightBrushKey, "#E3E3E3", "#2F3A47");
        Set(SystemColors.ControlLightLightBrushKey, "#FFFFFF", "#38424E");
        Set(SystemColors.MenuBrushKey, "#FFFFFF", "#1F2730");
        Set(SystemColors.MenuTextBrushKey, "#1B2430", "#E6EBF0");
        Set(SystemColors.HighlightBrushKey, "#CCE0F7", "#2F5F9F");
        Set(SystemColors.HighlightTextBrushKey, "#1B2430", "#FFFFFF");
        Set(SystemColors.InactiveSelectionHighlightBrushKey, "#E6F0FC", "#2B3A4D");
        Set(SystemColors.InactiveSelectionHighlightTextBrushKey, "#1B2430", "#E6EBF0");
        Set(SystemColors.GrayTextBrushKey, "#6D6D6D", "#7F8C99");
        Set(SystemColors.ScrollBarBrushKey, "#F0F0F0", "#252E39");
    }
}
