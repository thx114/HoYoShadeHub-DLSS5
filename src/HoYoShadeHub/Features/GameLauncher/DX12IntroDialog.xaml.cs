using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using HoYoShadeHub.Core.HoYoPlay;


namespace HoYoShadeHub.Features.GameLauncher;

[INotifyPropertyChanged]
public sealed partial class DX12IntroDialog : ContentDialog
{


    private GameDXConfig? _gameDXConfig;
    public GameDXConfig GameDXConfig
    {
        get => _gameDXConfig!;
        set
        {
            _gameDXConfig = value;
            OnPropertyChanged(nameof(GameDXConfig));
            // 没有官方预览图（鸣潮这种本地配置）就不铺两张空图
            if (PreviewPanel is not null)
            {
                PreviewPanel.Visibility = string.IsNullOrWhiteSpace(value?.DX12PreviewImage)
                    ? Microsoft.UI.Xaml.Visibility.Collapsed
                    : Microsoft.UI.Xaml.Visibility.Visible;
            }
        }
    }



    public DX12IntroDialog()
    {
        this.InitializeComponent();
    }



    [RelayCommand]
    private void Close()
    {
        this.Hide();
    }


}
