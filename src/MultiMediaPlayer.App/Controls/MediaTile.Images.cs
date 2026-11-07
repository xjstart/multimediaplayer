using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MultiMediaPlayer.App.Media;
using MultiMediaPlayer.Core;

namespace MultiMediaPlayer.App.Controls;

public sealed partial class MediaTile
{
    internal ImageBrowser? Browser { get; private set; }
    internal event Action<MediaTile>? ImageContentChanged;
    internal event Action<MediaTile>? ManageImagesRequested;
    internal event Action<string>? Notice;
    private Border? _imageNavigation;
    private Button? _previousImage, _nextImage, _manageImages;
    private TextBlock? _imageNumber;
    private bool _imageLoadFinished;

    private void SetupImageBrowser()
    {
        Browser = new(Item);
        Browser.Committed += (frame, reset) =>
        {
            if (_released) return;
            Error = null;
            if (_image == null)
            {
                _surface.Children.Clear(); _imageLayer.Children.Clear();
                _image = new Image { Stretch = Stretch.Fill };
                _imageLayer.Children.Add(_image); _surface.Children.Add(_imageLayer);
            }
            _image.Source = frame.Bitmap;
            _title.Text = System.IO.Path.GetFileName(Item.Path); _title.ToolTip = Item.Path;
            if (reset) _wheelRemainder = 0;
            ResizeViewport(); AttachImageNavigation();
            if (_imageLoadFinished) ImageContentChanged?.Invoke(this);
        };
        Browser.Changed += UpdateImageNavigation;
        Browser.Notice += message => Notice?.Invoke(message);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _previousImage = NavigationButton("‹", "上一张图片 · ←", async () => await Browser.NavigateAsync(-1));
        _imageNumber = new TextBlock { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new(3, 0, 3, 0) };
        _nextImage = NavigationButton("›", "下一张图片 · →", async () => await Browser.NavigateAsync(1));
        _manageImages = NavigationButton("⋯", "管理图片…", () => { ManageImagesRequested?.Invoke(this); return Task.CompletedTask; });
        buttons.Children.Add(_previousImage); buttons.Children.Add(_imageNumber); buttons.Children.Add(_nextImage); buttons.Children.Add(_manageImages);
        _imageNavigation = new Border { Child = buttons, Background = Brush("#DE171E29"), CornerRadius = new(5),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(2, 2, 2, 5) };
        Panel.SetZIndex(_imageNavigation, 5);
        MouseEnter += (_, _) => UpdateImageNavigation(); MouseLeave += (_, _) => UpdateImageNavigation();
        IsKeyboardFocusWithinChanged += (_, _) => UpdateImageNavigation();
        SizeChanged += (_, _) => UpdateImageNavigation();
        AttachImageNavigation();
    }

    private Button NavigationButton(string label, string tooltip, Func<Task> action)
    {
        var button = new Button { Content = label, ToolTip = tooltip, Padding = new(6, 1, 6, 1), Margin = new(1), MinWidth = 22, FontSize = 16 };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += async (_, e) => { e.Handled = true; SelectFromInput(this); await action(); };
        return button;
    }

    private void AttachImageNavigation()
    {
        if (_imageNavigation != null && !_surface.Children.Contains(_imageNavigation)) _surface.Children.Add(_imageNavigation);
        UpdateImageNavigation();
    }

    private void UpdateImageNavigation()
    {
        if (_imageNavigation == null || Browser == null) return;
        int index = ImageSequence.IndexOf(Browser.Paths, Item.Path);
        _previousImage!.IsEnabled = _imageLoadFinished && index > 0;
        _nextImage!.IsEnabled = _imageLoadFinished && index >= 0 && index < Browser.Paths.Count - 1;
        _manageImages!.IsEnabled = _imageLoadFinished;
        _imageNumber!.Text = $"{index + 1} / {Browser.Paths.Count}";
        _imageNumber.Visibility = ActualWidth < 165 ? Visibility.Collapsed : Visibility.Visible;
        _imageNavigation.ToolTip = $"{(Item.ImageCollectionPaths == null ? "当前文件夹" : "自定义图片集")} · {_imageNumber.Text}";
        _imageNavigation.Visibility = IsSelected || IsMouseOver || IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static bool IsInteractiveInput(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is ButtonBase or TextBoxBase or ComboBox or ComboBoxItem or Slider or ListBox or ListBoxItem or MenuItem) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }
}
