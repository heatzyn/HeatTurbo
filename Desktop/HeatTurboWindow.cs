using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Drawing;
using System.Windows.Forms;

namespace HeatTurbo.Desktop;

public sealed class HeatTurboWindow : Form
{
    private readonly Uri _appAddress;
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

    public HeatTurboWindow(Uri appAddress)
    {
        _appAddress = appAddress;
        Text = "HeatTurbo";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1024, 680);
        Size = new Size(1400, 900);
        BackColor = Color.FromArgb(8, 8, 8);
        Controls.Add(_loading);
        Shown += InitializeBrowserAsync;
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
            _webView.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) && IsAllowedExternalLink(target))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target.ToString()) { UseShellExecute = true });
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
        return uri.Host.EndsWith("nvidia.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("amd.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("intel.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("microsoft.com", StringComparison.OrdinalIgnoreCase);
    }
}
