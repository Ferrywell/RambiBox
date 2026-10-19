using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace RambiBox.App.Controls;

/// <summary>
/// Integer input with − / + buttons (like the WinUI NumberBox in "inline" spin mode).
/// Commits on Enter, focus loss, arrow keys, mouse wheel and the buttons; invalid text is reverted.
/// </summary>
public sealed class NumberBox : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(NumberBox),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((NumberBox)d).SyncText()));

    private readonly TextBox _text = new() { MinWidth = 64, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly RepeatButton _minus = new() { Content = "", Width = 32, Margin = new Thickness(4, 0, 0, 0), Focusable = false };
    private readonly RepeatButton _plus = new() { Content = "", Width = 32, Margin = new Thickness(4, 0, 0, 0), Focusable = false };

    public NumberBox()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var b in new[] { _minus, _plus })
        {
            b.SetResourceReference(Control.FontFamilyProperty, "IconFont");
            b.FontSize = 11;
            b.Interval = 90;
            b.Delay = 350;
            b.Padding = new Thickness(0);
        }
        SetColumn(_minus, 1);
        SetColumn(_plus, 2);
        Children.Add(_text);
        Children.Add(_minus);
        Children.Add(_plus);
        AutomationProperties.SetName(_minus, "Decrease");
        AutomationProperties.SetName(_plus, "Increase");

        _minus.Click += (_, _) => Step(-1);
        _plus.Click += (_, _) => Step(+1);
        _text.LostKeyboardFocus += (_, _) => CommitText();
        _text.PreviewKeyDown += OnKey;
        _text.PreviewMouseWheel += (_, e) =>
        {
            if (!_text.IsKeyboardFocusWithin) return;
            Step(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        };
        _text.GotKeyboardFocus += (_, _) => _text.SelectAll();
        SyncText();
    }

    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Minimum { get; set; }
    public int Maximum { get; set; } = 1000;

    /// <summary>Accessible name for the text box.</summary>
    public string Label
    {
        get => AutomationProperties.GetName(_text);
        set => AutomationProperties.SetName(_text, value);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                CommitText();
                _text.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape:
                SyncText();
                e.Handled = true;
                break;
            case Key.Up:
                Step(+1);
                e.Handled = true;
                break;
            case Key.Down:
                Step(-1);
                e.Handled = true;
                break;
        }
    }

    private void Step(int delta)
    {
        CommitText();
        Value = Math.Clamp(Value + delta, Minimum, Maximum);
        SyncText();
    }

    private void CommitText()
    {
        if (int.TryParse(_text.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int v))
        {
            v = Math.Clamp(v, Minimum, Maximum);
            if (v != Value) Value = v;
        }
        SyncText();
    }

    private void SyncText()
    {
        var s = Value.ToString(CultureInfo.CurrentCulture);
        if (_text.Text != s) _text.Text = s;
        _minus.IsEnabled = Value > Minimum;
        _plus.IsEnabled = Value < Maximum;
    }
}
