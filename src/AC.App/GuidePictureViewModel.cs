using System.Windows.Media.Imaging;
using AC.Core;

namespace AC.App;

/// <summary>A picture under a guide tip: shown once loaded, if it is more than an icon (<see cref="GuideParser.WorthShowing"/>).</summary>
public sealed class GuidePictureViewModel : ObservableObject
{
    private const int ThumbnailWidth = 320;

    public GuidePictureViewModel(string url)
    {
        Url = url;
        _ = LoadAsync();
    }

    public string Url { get; }

    private BitmapSource? _thumbnail;
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        private set { if (Set(ref _thumbnail, value)) OnPropertyChanged(nameof(IsShown)); }
    }

    public bool IsShown => _thumbnail is not null;

    private async Task LoadAsync()
    {
        var image = await ImageCache.GetAsync(Url, ThumbnailWidth);
        if (image is not null && ImageCache.SizeOf(Url) is { } size && GuideParser.WorthShowing(size.Width, size.Height)) Thumbnail = image;
    }
}
