using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace LinguaForge_X.Presentation.Wpf.Controls;

public partial class TrainingWebUiWindow : Window
{
    private readonly Uri _address;
    private bool _closed;
    private bool _initializing;

    public TrainingWebUiWindow(Uri address)
    {
        if (!IsAllowedNavigation(address.AbsoluteUri))
            throw new ArgumentException("训练界面只允许访问本机 GPT-SoVITS 服务。", nameof(address));
        _address = address;
        InitializeComponent();
    }

    // The upstream workbench opens its local labeling/UVR helpers on 9871–9873.
    public static bool IsAllowedNavigation(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp
        && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Host == "127.0.0.1" || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        && uri.Port is >= 9871 and <= 9874;

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await InitializeBrowserAsync();

    private async Task InitializeBrowserAsync()
    {
        if (_initializing || _closed) return;
        _initializing = true;
        try
        {
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LinguaForge-X", "training-webview");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            if (_closed) return;
            await Browser.EnsureCoreWebView2Async(environment);
            if (_closed) return;
            var core = Browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            // Upstream uses remote decorative badges; hide them when network resources are blocked.
            await core.AddScriptToExecuteOnDocumentCreatedAsync("""
                document.addEventListener('DOMContentLoaded', () => {
                    const style = document.createElement('style');
                    style.textContent = 'img[src^="https://"], img[src^="http://"]:not([src^="http://127.0.0.1:"]):not([src^="http://localhost:"]) { display: none !important; }';
                    document.head.appendChild(style);
                }, { once: true });
                """);
            if (_closed) return;
            core.NavigationStarting += (_, args) =>
            {
                if (IsAllowedNavigation(args.Uri)) return;
                args.Cancel = true;
                BrowserStatus.Text = "此工作台仅允许本机训练页面，已阻止离开本地服务。";
            };
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (IsAllowedNavigation(args.Uri)) core.Navigate(args.Uri);
                else BrowserStatus.Text = "已阻止打开非本机页面。";
            };
            core.PermissionRequested += (_, args) =>
            {
                args.State = CoreWebView2PermissionState.Deny;
                args.Handled = true;
                if (args.PermissionKind == CoreWebView2PermissionKind.Microphone)
                    BrowserStatus.Text = "请在主窗口使用「录音与校准」，检查音质后再导入训练。";
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                var uri = args.Request.Uri;
                if (IsAllowedNavigation(uri) || uri.StartsWith("blob:", StringComparison.Ordinal)
                    || uri.StartsWith("data:", StringComparison.Ordinal) || uri == "about:blank") return;
                args.Response = environment.CreateWebResourceResponse(null, 403, "Local workbench only", "");
            };
            core.NavigationCompleted += (_, args) =>
                BrowserStatus.Text = args.IsSuccess ? "本机训练界面已加载。训练结果需在「我的声音」导入成对模型并试听。"
                    : "页面未能载入。请确认训练服务仍在运行，必要时返回主窗口重新启动。";
            core.ProcessFailed += (_, _) => BrowserStatus.Text = "训练界面的浏览器进程已退出。请关闭此窗口，再从主窗口重新打开。";
            core.Navigate(_address.AbsoluteUri);
        }
        catch (Exception error) when (!_closed)
        {
            BrowserStatus.Text = error is WebView2RuntimeNotFoundException
                ? "缺少 Microsoft Edge WebView2 Runtime。安装微软 WebView2 Evergreen Runtime 后重新打开此界面。训练服务可在主窗口停止。"
                : "无法打开内嵌训练界面，请检查 WebView2 Runtime 和本地目录权限。训练服务可在主窗口停止。";
        }
        catch (Exception) when (_closed) { }
        finally { _initializing = false; }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is { } core) core.Navigate(_address.AbsoluteUri);
        else await InitializeBrowserAsync();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        Browser.Dispose();
    }
}
