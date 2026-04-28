using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CascadeLauncher.Services;
using CascadeLauncher.Views;

namespace CascadeLauncher.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly AuthService _auth;
    private readonly LaunchOrchestrator _orchestrator;

    public MainViewModel()
    {
        _auth = new AuthService(App.RuntimeDir);
        _orchestrator = new LaunchOrchestrator(App.RuntimeDir, _auth);

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText = v == null ? "v0.1" : $"v{v.Major}.{v.Minor}.{v.Build}";

        // If we already have a stored profile, show the username on the launch button.
        var stored = _auth.LoadStoredProfile();
        if (stored != null)
        {
            _profile = stored;
            UpdateLaunchButton();
        }
    }

    // ---- bindable state ----

    private string _statusText = "Ready.";
    public string StatusText { get => _statusText; private set { _statusText = value; OnChanged(); } }

    private double _progress;
    public double Progress { get => _progress; private set { _progress = value; OnChanged(); } }

    private bool _isIndeterminate = true;
    public bool IsIndeterminate { get => _isIndeterminate; private set { _isIndeterminate = value; OnChanged(); } }

    private Visibility _progressVisibility = Visibility.Collapsed;
    public Visibility ProgressVisibility { get => _progressVisibility; private set { _progressVisibility = value; OnChanged(); } }

    private bool _canLaunch = true;
    public bool CanLaunch { get => _canLaunch; private set { _canLaunch = value; OnChanged(); } }

    private string _launchButtonText = "Launch";
    public string LaunchButtonText { get => _launchButtonText; private set { _launchButtonText = value; OnChanged(); } }

    public string VersionText { get; }

    private MinecraftProfile? _profile;

    // ---- commands ----

    public async Task LaunchAsync()
    {
        if (!CanLaunch) return;
        CanLaunch = false;
        ProgressVisibility = Visibility.Visible;
        IsIndeterminate = true;

        try
        {
            // Make sure we have a Microsoft-authenticated profile. Prompt if not.
            if (_profile == null)
            {
                _profile = await PromptLoginAsync();
                if (_profile == null)
                {
                    StatusText = "Sign-in cancelled.";
                    return;
                }
                UpdateLaunchButton();
            }
            else
            {
                // Silently refresh the MS token on each launch. If it fails,
                // wipe the stored profile and re-prompt — the in-game session
                // would reject a stale token anyway.
                var refreshed = await _auth.TryRefreshAsync(_profile, CancellationToken.None);
                if (refreshed != null)
                {
                    _profile = refreshed;
                    UpdateLaunchButton();
                }
                else
                {
                    Logger.Warn("token refresh returned null; reprompting for sign-in");
                    _auth.ClearProfile();
                    _profile = await PromptLoginAsync();
                    if (_profile == null)
                    {
                        StatusText = "Sign-in cancelled.";
                        return;
                    }
                    UpdateLaunchButton();
                }
            }

            var status = new Progress<string>(s => StatusText = s);
            await _orchestrator.LaunchAsync(_profile!, status, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            Logger.Error("launch failed", ex);
            StatusText = $"Error: {ex.Message}";
            MessageBox.Show(ex.Message, "Launch failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CanLaunch = true;
            ProgressVisibility = Visibility.Collapsed;
        }
    }

    private Task<MinecraftProfile?> PromptLoginAsync()
    {
        var dlg = new LoginWindow(_auth) { Owner = Application.Current.MainWindow };
        var ok = dlg.ShowDialog() == true;
        return Task.FromResult(ok ? dlg.Result : null);
    }

    private void UpdateLaunchButton()
    {
        if (_profile == null) { LaunchButtonText = "Launch"; return; }
        LaunchButtonText = $"Launch  ·  {_profile.Username}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
