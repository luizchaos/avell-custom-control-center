using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AvellCC.App;

/// <summary>
/// On-screen notice (like the vendor's OSD) for the mode button: a small topmost popup at the bottom of the screen that
/// fades out after a few seconds. Used instead of tray balloons, which Windows hides when notifications or
/// "Do not disturb" are off. Never takes focus.
/// </summary>
public sealed class OsdPopup : Window
{
    private readonly TextBlock _title = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA1, 0xAD)) };
    private readonly TextBlock _text = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(2.5) };

    public OsdPopup()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        var icon = new Image { Width = 40, Height = 40, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        using (var bmp = AppIcon.Render(80))
        {
            var handle = bmp.GetHbitmap();
            try
            {
                icon.Source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            }
            finally { DeleteObject(handle); }
        }
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(_title);
        texts.Children.Add(_text);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(texts);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x16, 0x18, 0x1D)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 12, 24, 12),
            Child = row,
        };
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400));
            fade.Completed += (_, _) => { if (!_hide.IsEnabled) Hide(); };
            BeginAnimation(OpacityProperty, fade);
        };
    }

    public void ShowMessage(string title, string text)
    {
        _title.Text = title;
        _text.Text = text;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (!IsVisible) Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 80;
        _hide.Stop();
        _hide.Start();
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
