using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CascadeLauncher.Services;

namespace CascadeLauncher.Views;

public partial class LoginWindow : Window
{
    private readonly AuthService _auth;
    private CancellationTokenSource? _cts;

    public MinecraftProfile? Result { get; private set; }

    public LoginWindow(AuthService auth)
    {
        _auth = auth;
        InitializeComponent();
        MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) try { DragMove(); } catch { } };
    }

    private async void BtnMicrosoft_Click(object sender, RoutedEventArgs e)
    {
        // First-time setup: collect the Azure client ID inline before kicking off OAuth.
        if (!_auth.IsConfigured)
        {
            ChoicePanel.Visibility = Visibility.Collapsed;
            SetupPanel.Visibility = Visibility.Visible;
            ClientIdBox.Focus();
            return;
        }

        await BeginDeviceFlowAsync();
    }

    private void BtnSaveClientId_Click(object sender, RoutedEventArgs e)
    {
        ClientIdError.Visibility = Visibility.Collapsed;
        try
        {
            _auth.SetClientId(ClientIdBox.Text);
        }
        catch (Exception ex)
        {
            ClientIdError.Text = ex.Message;
            ClientIdError.Visibility = Visibility.Visible;
            return;
        }
        SetupPanel.Visibility = Visibility.Collapsed;
        _ = BeginDeviceFlowAsync();
    }

    private async Task BeginDeviceFlowAsync()
    {
        // Hide the other panes — they share a Grid cell with DevicePanel, so
        // leaving them visible would render on top of the device-code UI.
        ChoicePanel.Visibility = Visibility.Collapsed;
        SetupPanel.Visibility = Visibility.Collapsed;
        DevicePanel.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();

        try
        {
            var start = await _auth.StartDeviceLoginAsync(_cts.Token);
            DeviceUrl.Text = start.VerificationUri;
            DeviceCode.Text = start.UserCode;
            DeviceStatus.Text = "Waiting for sign-in…";

            // Open the verification URL in the user's browser as a convenience.
            try { Process.Start(new ProcessStartInfo(start.VerificationUri) { UseShellExecute = true }); }
            catch { /* user can copy/paste */ }

            var profile = await _auth.CompleteDeviceLoginAsync(start, _cts.Token);
            Result = profile;
            DialogResult = true;
        }
        catch (OperationCanceledException) { /* user cancelled — stay open */ }
        catch (Exception ex)
        {
            // Always persist auth failures to launcher.log; the on-screen text
            // is short-form and we want full stack traces for diagnosis.
            Logger.Error("device-code login failed", ex);
            DeviceStatus.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
            DeviceStatus.Text = ex.Message;
        }
    }

    private void DeviceUrl_Click(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(DeviceUrl.Text) { UseShellExecute = true }); } catch { }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        DialogResult = false;
    }
}
