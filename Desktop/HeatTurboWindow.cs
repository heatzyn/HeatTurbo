using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HeatTurbo.Desktop;

public sealed class HeatTurboWindow : Form
{
    private readonly Uri _appAddress;
    private readonly string _apiToken;
    private readonly Func<bool> _isCriticalOperationRunning;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly Label _loading = new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        Text = "HEATTURBO\n\nINICIANDO ENGINE...",
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.White,
        BackColor = Color.FromArgb(8, 8, 8),
        Font = new Font("Segoe UI", 16, FontStyle.Bold)
    };

    public HeatTurboWindow(Uri appAddress, string apiToken, Func<bool> isCriticalOperationRunning)
    {
        _appAddress = appAddress;
        _apiToken = apiToken;
        _isCriticalOperationRunning = isCriticalOperationRunning;
        Text = "HeatTurbo";
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath)) Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1024, 680);
        Size = new Size(1400, 900);
        BackColor = Color.FromArgb(8, 8, 8);
        Controls.Add(_loading);
        Shown += InitializeBrowserAsync;
        FormClosing += PreventCloseDuringCriticalOperation;
    }

    private async void InitializeBrowserAsync(object? sender, EventArgs e)
    {
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HeatTurbo", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await _webView.EnsureCoreWebView2Async(environment);

            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            _webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            _webView.CoreWebView2.Settings.AreHostObjectsAllowed = true;
            _webView.CoreWebView2.AddHostObjectToScript("heatTurbo", new HeatTurboHostBridge(_apiToken));
            _webView.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) && IsAllowedExternalLink(target))
                    TryOpenExternal(target);
            };
            _webView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) || IsLocalAppAddress(target)) return;
                args.Cancel = true;
                if (IsAllowedExternalLink(target)) TryOpenExternal(target);
            };
            _webView.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess) ShowStartupError($"Falha ao carregar a interface ({args.WebErrorStatus}).");
            };

            Controls.Remove(_loading);
            Controls.Add(_webView);
            _webView.BringToFront();
            _webView.Source = _appAddress;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowStartupError("O Microsoft Edge WebView2 Runtime não foi encontrado. Instale-o e abra o HeatTurbo novamente.");
        }
        catch (Exception ex)
        {
            ShowStartupError($"Não foi possível iniciar o HeatTurbo.\n\n{ex.Message}");
        }
    }

    private void PreventCloseDuringCriticalOperation(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason != CloseReason.UserClosing || !_isCriticalOperationRunning()) return;
        e.Cancel = true;
        MessageBox.Show(
            "O Windows ainda está instalando drivers. Aguarde o resultado na aba Drivers antes de fechar o HeatTurbo.",
            "Instalação de drivers em andamento",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    public sealed class HeatTurboHostBridge
    {
        public HeatTurboHostBridge(string apiToken) => ApiToken = apiToken;
        public string ApiToken { get; }
    }

    private void ShowStartupError(string message)
    {
        _loading.Text = message;
        if (!Controls.Contains(_loading))
        {
            Controls.Add(_loading);
            _loading.BringToFront();
        }
    }

    private static bool IsAllowedExternalLink(Uri uri)
    {
        if (uri.Scheme == "ms-settings") return true;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        return IsHostOrSubdomain(uri.Host, "nvidia.com")
            || IsHostOrSubdomain(uri.Host, "amd.com")
            || IsHostOrSubdomain(uri.Host, "intel.com")
            || IsHostOrSubdomain(uri.Host, "microsoft.com");
    }

    private bool IsLocalAppAddress(Uri uri) =>
        uri.Scheme.Equals(_appAddress.Scheme, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals(_appAddress.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == _appAddress.Port;

    private static bool IsHostOrSubdomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);

    private static void TryOpenExternal(Uri target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target.ToString())
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // An unavailable browser/settings handler must not terminate HeatTurbo.
        }
    }
}
