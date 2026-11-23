using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SabhaMujic.Desktop.Controls;

/// <summary>
/// Single-line text with letter-spacing (WPF's TextBlock has none). Used for the uppercase header subtitle, which the
/// design guide tracks at 0.12em. Font family, size, weight and foreground are inherited like a TextBlock's.
/// </summary>
public sealed class LetterSpacedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LetterSpacedText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Extra space after each letter, in em (0.12 = 12 % of the font size).</summary>
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(LetterSpacedText),
        new FrameworkPropertyMetadata(0.12, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
        typeof(LetterSpacedText),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(LetterSpacedText),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontSize, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(
        typeof(LetterSpacedText),
        new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(LetterSpacedText),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = 0.0;
        var height = 0.0;
        foreach (var glyph in Layout())
        {
            width = glyph.X + glyph.Text.WidthIncludingTrailingWhitespace;
            height = Math.Max(height, glyph.Text.Height);
        }

        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        foreach (var glyph in Layout())
        {
            drawingContext.DrawText(glyph.Text, new Point(glyph.X, 0));
        }
    }

    private IEnumerable<(FormattedText Text, double X)> Layout()
    {
        var text = Text ?? "";
        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var x = 0.0;
        foreach (var ch in text)
        {
            var formatted = new FormattedText(
                ch.ToString(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, FontSize, Foreground, pixelsPerDip);
            yield return (formatted, x);
            x += formatted.WidthIncludingTrailingWhitespace + (Spacing * FontSize);
        }
    }
}
