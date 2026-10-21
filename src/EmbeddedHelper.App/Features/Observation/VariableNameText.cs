using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using EmbeddedHelper.Core;

namespace EmbeddedHelper.App;

public sealed class VariableNameText : TextBlock
{
    public static readonly DependencyProperty FullNameProperty = DependencyProperty.Register(nameof(FullName), typeof(string), typeof(VariableNameText), new PropertyMetadata("", Changed));
    public string FullName { get => (string)GetValue(FullNameProperty); set => SetValue(FullNameProperty, value); }
    public VariableNameText() { TextWrapping = TextWrapping.NoWrap; TextTrimming = TextTrimming.CharacterEllipsis; SizeChanged += (_, _) => RefreshName(); Loaded += (_, _) => RefreshName(); }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((VariableNameText)sender).RefreshName();
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); RefreshName(); }
    static VariableNameText()
    {
        foreach (var property in new[] { FontSizeProperty, FontFamilyProperty, FontWeightProperty, FontStyleProperty, FontStretchProperty })
            property.OverrideMetadata(typeof(VariableNameText), new FrameworkPropertyMetadata(property.GetMetadata(typeof(TextBlock)).DefaultValue, FrameworkPropertyMetadataOptions.AffectsMeasure, Changed));
    }
    private void RefreshName()
    {
        var name = FullName ?? "";
        AutomationProperties.SetName(this, name);
        Text = !IsLoaded || ActualWidth <= 0 ? name : VariableNameDisplay.Fit(name, ActualWidth,
            text => new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground,
                VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace);
    }
}
