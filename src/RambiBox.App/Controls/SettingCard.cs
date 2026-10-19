using System.Windows;
using System.Windows.Controls;

namespace RambiBox.App.Controls;

/// <summary>
/// A Windows 11 Settings style row: optional icon, title, one-sentence description (also the tooltip)
/// and the control (Content) on the right. Template in Themes/Styles.xaml.
/// </summary>
public sealed class SettingCard : ContentControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SettingCard), new PropertyMetadata(""));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingCard), new PropertyMetadata(""));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(SettingCard), new PropertyMetadata(""));

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    static SettingCard()
    {
        FocusableProperty.OverrideMetadata(typeof(SettingCard), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(SettingCard), new FrameworkPropertyMetadata(false));
    }
}
