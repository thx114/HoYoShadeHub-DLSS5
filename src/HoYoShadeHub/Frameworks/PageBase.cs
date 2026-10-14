using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Services;
using System.Threading;

namespace HoYoShadeHub.Frameworks;

[INotifyPropertyChanged]
public abstract partial class PageBase : Page
{
    private readonly UiLifetime _lifetime = new();
    protected CancellationToken PageCancellationToken => _lifetime.Token;
    protected long PageGeneration => _lifetime.Generation;
    protected bool IsPageGenerationCurrent(long generation) => _lifetime.IsCurrent(generation);


    public GameId CurrentGameId { get; protected set => SetProperty(ref field, value); }


    public GameBiz CurrentGameBiz { get; protected set => SetProperty(ref field, value); }



    public PageBase()
    {
        Loaded += PageEx_Loaded;
        Unloaded += PageEx_Unloaded;
    }



    private void PageEx_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_lifetime.Enter()) return;
        try { OnLoaded(); }
        catch { _lifetime.Exit(); throw; }
    }


    private void PageEx_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_lifetime.Exit()) OnUnloaded();
    }



    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is GameId id)
        {
            CurrentGameId = id;
            CurrentGameBiz = id.GameBiz;
        }
        else if (e.Parameter is GameBiz biz)
        {
            CurrentGameBiz = biz;
            CurrentGameId = GameId.FromGameBiz(biz)!;
        }
        else
        {
            CurrentGameId = null!;
            CurrentGameBiz = default;
        }
    }



    protected virtual void OnLoaded()
    {

    }



    protected virtual void OnUnloaded()
    {

    }


}
