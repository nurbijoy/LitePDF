using System.Windows.Media.Imaging;
using LitePdf.Core;

namespace LitePdf.App.ViewModels;

public sealed class ThumbnailViewModel : ObservableObject
{
    public int Index { get; }
    public PageSize PageSize { get; }

    private WriteableBitmap? _bitmap;
    public WriteableBitmap? Bitmap
    {
        get => _bitmap;
        set => SetProperty(ref _bitmap, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ThumbnailViewModel(int index, PageSize size)
    {
        Index = index;
        PageSize = size;
    }
}
