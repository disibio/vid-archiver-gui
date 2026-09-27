using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.ViewModels;

/// <summary>
/// Live status for a long-running tool job (installing, updating): what it's doing, how far a download has got, how
/// long it has been waiting, and a way to cancel it. Updated every second so a stuck step is visible as stuck.
/// </summary>
public partial class BusyStatusViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _cts;
    private string _what = "";
    private TransferProgress? _transfer;
    private DateTime _started, _lastData;
    private Action<string>? _onText;

    public BusyStatusViewModel() => _timer.Tick += (_, _) => Update();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isActive;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _isIndeterminate = true;

    /// <summary>
    /// Starts showing <paramref name="what"/>; the returned token is cancelled by the Cancel button.
    /// <paramref name="onText"/> also gets every change of the text until <see cref="Stop"/>.
    /// </summary>
    public CancellationToken Start(string what, Action<string>? onText = null)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _what = what;
        _onText = onText;
        _transfer = null;
        _started = _lastData = DateTime.UtcNow;
        IsActive = true;
        Update();
        _timer.Start();
        return _cts.Token;
    }

    /// <summary>Feed download progress here; it replaces the plain "what" text while a download is running.</summary>
    public IProgress<TransferProgress> Progress => new Progress<TransferProgress>(p =>
    {
        if (p.Received != _transfer?.Received || p.Connecting != _transfer?.Connecting)
        {
            _lastData = DateTime.UtcNow;
        }

        _transfer = p;
        Update();
    });

    public void Stop()
    {
        _onText = null;
        _timer.Stop();
        IsActive = false;
        Text = "";
        Percent = 0;
    }

    partial void OnTextChanged(string value) => _onText?.Invoke(value);

    public bool WasCancelled => _cts?.IsCancellationRequested == true;

    [RelayCommand(CanExecute = nameof(IsActive))]
    private void Cancel()
    {
        _cts?.Cancel();
        Text = "Cancelling…";
    }

    private void Update()
    {
        if (_cts?.IsCancellationRequested == true)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (_transfer is null)
        {
            var seconds = (int)(now - _started).TotalSeconds;
            Text = seconds < 2 ? $"{_what}…" : $"{_what}… {seconds} s";
            IsIndeterminate = true;
            return;
        }

        var quiet = (int)(now - _lastData).TotalSeconds;
        Text = quiet >= 5 ? $"{_transfer} — no data for {quiet} s" : _transfer.ToString();
        IsIndeterminate = _transfer.Fraction is null;
        Percent = (_transfer.Fraction ?? 0) * 100;
    }
}
