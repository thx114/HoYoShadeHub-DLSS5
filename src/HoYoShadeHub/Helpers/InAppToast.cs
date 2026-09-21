using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Xaml.Interactivity;
using System;
using System.Threading.Tasks;

namespace HoYoShadeHub.Helpers;

public class InAppToast : Behavior<StackPanel>
{

    private readonly DispatcherQueueTimer _dismissTimer;


    public string Tag
    {
        get { return (string)GetValue(TagProperty); }
        set { SetValue(TagProperty, value); }
    }
    public static readonly DependencyProperty TagProperty =
        DependencyProperty.Register("Tag", typeof(string), typeof(InAppToast), new PropertyMetadata(default));


    public static InAppToast? MainWindow { get; private set; }




    public InAppToast()
    {
        _dismissTimer = DispatcherQueue.CreateTimer();
        _dismissTimer.Interval = TimeSpan.FromSeconds(30);
        _dismissTimer.IsRepeating = true;
        _dismissTimer.Tick += _dismissTimer_Tick;
    }


    protected override void OnAttached()
    {
        base.OnAttached();
        if (Tag is nameof(MainWindow))
        {
            MainWindow = this;
        }
    }


    protected override void OnDetaching()
    {
        base.OnDetaching();
        if (Tag is nameof(MainWindow))
        {
            MainWindow = null;
        }
    }



    private void _dismissTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            int i = 0;
            var count = AssociatedObject.Children.Count;
            while (i < count)
            {
                var item = AssociatedObject.Children[i] as InfoBar;
                if (item != null && !item.IsOpen)
                {
                    AssociatedObject.Children.RemoveAt(i);
                    count--;
                }
                else
                {
                    i++;
                }
            }
        }
        catch { }
    }



    public void Show(InfoBar infoBar, int duration = 0, int index = -1)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                infoBar.IsOpen = true;
                if (index > 0)
                {
                    AssociatedObject.Children.Insert(index, infoBar);
                }
                else
                {
                    AssociatedObject.Children.Add(infoBar);
                }
                if (duration > 0)
                {
                    await Task.Delay(duration);
                    infoBar.IsOpen = false;
                }
            }
            catch { }
        });
    }


    private void AddInfoBar(InfoBarSeverity severity, string? title, string? message, int duration = 0)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var infoBar = new InfoBar
            {
                Title = title,
                Message = message,
                Severity = severity,
                IsOpen = true,
            };
            if (severity == InfoBarSeverity.Informational)
            {
                infoBar.Background = Application.Current.Resources["CustomAcrylicBrush"] as Brush;
            }
            Show(infoBar, duration);
        });
    }




    public void Information(string? title, string? message = null, int duration = 3000)
    {
        AddInfoBar(InfoBarSeverity.Informational, title, message, duration);
    }



    public void Success(string? title, string? message = null, int duration = 3000)
    {
        AddInfoBar(InfoBarSeverity.Success, title, message, duration);
    }




    public void Warning(string? title, string? message = null, int duration = 5000)
    {
        AddInfoBar(InfoBarSeverity.Warning, title, message, duration);
    }



    public void Error(string? title, string? message = null, int duration = 5000)
    {
        AddInfoBar(InfoBarSeverity.Error, title, message, duration);
    }



    public void Error(Exception ex, string? message = null, int duration = 5000)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            AddInfoBar(InfoBarSeverity.Error, ex.GetType().Name, ex.Message, duration);
        }
        else
        {
            AddInfoBar(InfoBarSeverity.Error, $"{ex.GetType().Name} - {message}", ex.Message, duration);
        }
    }


    /// <summary>
    /// 常驻提示条：**不自动关**，标题右边挂一个可点击的文本按钮（默认红色 = 危险操作）。
    /// 返回这条 <see cref="InfoBar"/>，调用方想在结束时收掉就把它的 <c>IsOpen</c> 设成 false
    /// （那个 30 秒的清理定时器会把关掉的条目摘掉）。
    ///
    /// <para><b>必须在 UI 线程上调用</b>（它会直接往提示条容器里加控件）。</para>
    /// </summary>
    public InfoBar? ShowSticky(string? title, string? actionText = null, Action? action = null, bool danger = true)
    {
        try
        {
            var infoBar = new InfoBar
            {
                Title = title,
                Severity = InfoBarSeverity.Informational,
                Background = Application.Current.Resources["CustomAcrylicBrush"] as Brush,
                IsOpen = true,
            };

            if (!string.IsNullOrWhiteSpace(actionText) && action is not null)
            {
                var button = new Button
                {
                    Content = actionText,
                    Background = null,
                    BorderThickness = new Thickness(0),
                    MinWidth = 0,
                    Padding = new Thickness(6, 2, 6, 2),
                };

                if (danger)
                {
                    button.Foreground = Application.Current.Resources["SystemFillColorCriticalBrush"] as Brush;
                }

                button.Click += (_, _) =>
                {
                    try
                    {
                        action();
                    }
                    catch
                    {
                        // ignore
                    }
                };

                infoBar.ActionButton = button;
            }

            AssociatedObject.Children.Add(infoBar);
            return infoBar;
        }
        catch
        {
            return null;
        }
    }



    public void ShowWithButton(InfoBarSeverity severity, string? title, string? message, string buttonContent, Action buttonAction, Action? closedAction = null, int duration = 0)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var infoBar = Create(severity, title, message, buttonContent, buttonAction, closedAction);
            Show(infoBar, duration);
        });
    }


    private InfoBar Create(InfoBarSeverity severity, string? title, string? message = null, string? buttonContent = null, Action? buttonAction = null, Action? closedAction = null)
    {
        Button? button = null;
        if (!string.IsNullOrWhiteSpace(buttonContent) && buttonAction != null)
        {
            button = new Button
            {
                Content = buttonContent,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            button.Click += (_, _) =>
            {
                try
                {
                    buttonAction();
                }
                catch { }
            };
        }
        var infoBar = new InfoBar
        {
            Severity = severity,
            Title = title,
            Message = message,
            ActionButton = button,
            IsOpen = true,
        };
        if (closedAction is not null)
        {
            infoBar.CloseButtonClick += (_, _) =>
            {
                try
                {
                    closedAction();
                }
                catch { }
            };
        }
        return infoBar;
    }



}
