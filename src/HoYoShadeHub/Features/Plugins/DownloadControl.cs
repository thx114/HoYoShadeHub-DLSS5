using HoYoShadeHub.Extensions.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// Drives one cancellable/pausable download and wires it to a
/// <see cref="ProgressBar"/> + status <see cref="TextBlock"/> + a cancel button.
///
/// Why this exists: <c>DownloadService</c> always accepted a CancellationToken, but no page ever
/// created a CancellationTokenSource, so every download passed CancellationToken.None and could
/// not be stopped. This type is the missing piece.
///
/// Usage:
/// <code>
/// using var job = DownloadJob.Start(progressBar, statusText, cancelButton);
/// await something.DownloadAsync(job.Progress, job.Token, job.Pause);
/// </code>
/// The token is disposed on Dispose, which also unsubscribes the button.
/// </summary>
public sealed class DownloadJob : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly DownloadPauseToken _pause = new();
    private readonly ProgressBar? _bar;
    private readonly TextBlock? _status;
    private readonly Button? _cancelButton;
    private readonly string _label;

    private long _lastBytes;
    private double _lastPercent;
    private int _disposed;

    private DownloadJob(ProgressBar? bar, TextBlock? status, Button? cancelButton, string label)
    {
        _bar = bar;
        _status = status;
        _cancelButton = cancelButton;
        _label = label;

        if (_bar is not null)
        {
            _bar.Minimum = 0;
            _bar.Maximum = 100;
            _bar.IsIndeterminate = true;
            _bar.Value = 0;
            _bar.Visibility = Visibility.Visible;
        }

        if (_cancelButton is not null)
        {
            _cancelButton.Visibility = Visibility.Visible;
            _cancelButton.Click += OnCancelClick;
        }

        SetStatus($"{_label}...");
    }

    public static DownloadJob Start(ProgressBar? bar, TextBlock? status, Button? cancelButton, string label = "\u6b63\u5728\u4e0b\u8f7d")
        => new(bar, status, cancelButton, label);

    public CancellationToken Token => _cts.Token;

    public DownloadPauseToken Pause => _pause;

    public bool IsCancellationRequested => _cts.IsCancellationRequested;

    public bool IsPaused => _pause.IsPaused;

    /// <summary>Raised after the user pauses or resumes, so the caller can refresh its own UI.</summary>
    public event EventHandler? PauseStateChanged;

    public IProgress<DownloadProgress> Progress => new Progress<DownloadProgress>(OnProgress);

    private void OnProgress(DownloadProgress p)
    {
        if (_bar is not null)
        {
            if (p.Percent is null)
            {
                _bar.IsIndeterminate = true;
            }
            else
            {
                _bar.IsIndeterminate = false;
                _bar.Value = p.Percent.Value;
            }
        }

        _lastBytes = p.BytesReceived;
        _lastPercent = p.Percent ?? 0;

        if (p.Percent is null)
        {
            SetStatus($"{_label} {FormatSize(p.BytesReceived)}");
        }
        else
        {
            SetStatus($"{_label} {p.Percent.Value:F0}%  ({FormatSize(p.BytesReceived)} / {FormatSize(p.TotalBytes ?? 0)})");
        }
    }

    /// <summary>Called by the cancel button: first click while running cancels the download.</summary>
    private void OnCancelClick(object sender, RoutedEventArgs e) => Cancel();

    public void Cancel()
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        // Resume first, otherwise a paused download can never observe the cancellation.
        _pause.Resume();
        _cts.Cancel();
        SetStatus("\u6b63\u5728\u53d6\u6d88...");
    }

    public void TogglePause()
    {
        if (_pause.IsPaused)
        {
            _pause.Resume();
        }
        else
        {
            _pause.Pause();
            SetStatus($"\u5df2\u6682\u505c  ({FormatSize(_lastBytes)}{(_lastPercent > 0 ? $" / {_lastPercent:F0}%" : string.Empty)})");
        }

        PauseStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Marks the job as finished and hides the cancel button.</summary>
    public void Complete(string? finalStatus = null)
    {
        if (_bar is not null)
        {
            _bar.IsIndeterminate = false;
            _bar.Visibility = Visibility.Collapsed;
        }

        if (_cancelButton is not null)
        {
            _cancelButton.Visibility = Visibility.Collapsed;
        }

        if (finalStatus is not null)
        {
            SetStatus(finalStatus);
        }
    }

    public string ProgressSummary =>
        _lastPercent > 0 ? $"{_lastPercent:F0}%  ({FormatSize(_lastBytes)})" : FormatSize(_lastBytes);

    private void SetStatus(string text)
    {
        if (_status is not null)
        {
            _status.Text = text;
        }
    }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:F0} {units[unit]}" : $"{value:F1} {units[unit]}";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_cancelButton is not null)
        {
            _cancelButton.Click -= OnCancelClick;
        }

        _cts.Dispose();
    }
}