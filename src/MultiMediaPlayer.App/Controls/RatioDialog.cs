using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace MultiMediaPlayer.App.Controls;
public sealed class RatioDialog : Window
{
    public double Ratio { get; private set; }
    public RatioDialog(double current, string label)
    {
        Title = "分区比例"; Width = 390; Height = 230; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        var stack = new StackPanel { Margin = new(24) };
        stack.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        var input = new TextBox { Text = $"{current * 100:0.#}:{(1 - current) * 100:0.#}" }; stack.Children.Add(input);
        var error = new TextBlock { Margin = new(0, 8, 0, 0), Foreground = System.Windows.Media.Brushes.Salmon }; stack.Children.Add(error);
        var apply = new Button { Content = "应用比例", IsDefault = true, Margin = new(0, 10, 0, 0) };
        apply.Click += (_, _) =>
        {
            var parts = input.Text.Replace('：', ':').Split(':');
            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
                && double.IsFinite(a + b) && a > 0 && b > 0 && a / (a + b) >= .05 && a / (a + b) <= .95)
            { Ratio = a / (a + b); DialogResult = true; }
            else error.Text = "请输入如 60:40 的比例，每侧至少占 5%。";
        };
        stack.Children.Add(apply); Content = stack;
        Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
    }
}
