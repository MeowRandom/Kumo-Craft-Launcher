using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Kumo.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Kumo.Controls;

public partial class PlayerModel3DView : UserControl
{
    private bool _initializing;
    private bool _webReady;
    private int _rendererVersion;
    private WebView2? _browser;
    private string? _skinPath;
    private string? _capePath;
    private bool _slim;
    private readonly Dictionary<string, string> _dataUrls =
        new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<int>? WheelScrolled;

    public PlayerModel3DView()
    {
        InitializeComponent();
    }

    public void SetAppearance(string? skinPath, string? capePath, bool slim)
    {
        _skinPath = skinPath;
        _capePath = capePath;
        _slim = slim;
        SendAppearance();
    }

    public void ClearAppearance()
    {
        _skinPath = null;
        _capePath = null;
        SendAppearance();
    }

    public void ResetView()
    {
        if (_webReady && _browser is { } browser)
            _ = browser.ExecuteScriptAsync("window.resetSkinView && window.resetSkinView();");
    }

    public void DisposeRenderer()
    {
        ResetRenderer();
    }

    private void ResetRenderer()
    {
        _rendererVersion++;
        _webReady = false;
        _initializing = false;
        var browser = _browser;
        _browser = null;
        BrowserHost.Children.Clear();
        BrowserHost.Visibility = Visibility.Collapsed;
        try
        {
            browser?.Dispose();
        }
        catch
        {
            // The native controller may already have been released during shutdown.
        }
    }

    private void PlayerModel3DView_Loaded(object sender, RoutedEventArgs e)
    {
        if (IsVisible) _ = InitializeAsync();
    }

    private void PlayerModel3DView_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) return;
        if (_webReady && _browser is { } browser)
        {
            BrowserHost.Visibility = Visibility.Visible;
            browser.Visibility = Visibility.Visible;
            _ = browser.ExecuteScriptAsync(
                "window.resizeSkinView && window.resizeSkinView();");
            return;
        }
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        if (_webReady && _browser is { } readyBrowser)
        {
            BrowserHost.Visibility = Visibility.Visible;
            readyBrowser.Visibility = Visibility.Visible;
            return;
        }
        if (_initializing) return;
        var version = ++_rendererVersion;
        _initializing = true;
        LoadingText.Text = "正在加载 3D 预览…";
        LoadingPanel.Visibility = Visibility.Visible;
        try
        {
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kumo", "webview2");
            Directory.CreateDirectory(dataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            if (version != _rendererVersion) return;
            var browser = new WebView2
            {
                Visibility = Visibility.Collapsed,
                DefaultBackgroundColor = System.Drawing.Color.Transparent
            };
            _browser = browser;
            BrowserHost.Children.Add(browser);
            BrowserHost.Visibility = Visibility.Visible;
            await browser.EnsureCoreWebView2Async(environment);
            if (version != _rendererVersion ||
                !ReferenceEquals(_browser, browser) ||
                browser.CoreWebView2 is not { } coreWebView)
                return;
            var settings = coreWebView.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            coreWebView.WebMessageReceived += OnWebMessageReceived;
            coreWebView.NavigationCompleted += (_, args) =>
            {
                if (version != _rendererVersion || !ReferenceEquals(_browser, browser))
                    return;
                if (!args.IsSuccess)
                {
                    LoadingText.Text = "3D 预览加载失败";
                    LoadingPanel.Visibility = Visibility.Visible;
                    browser.Visibility = Visibility.Collapsed;
                    return;
                }
                browser.Visibility = Visibility.Visible;
                _ = browser.ExecuteScriptAsync(
                    "window.resizeSkinView && window.resizeSkinView();");
            };
            browser.Visibility = Visibility.Visible;
            browser.NavigateToString(BuildHtml());
        }
        catch (Exception ex)
        {
            if (version != _rendererVersion) return;
            LoadingText.Text = "3D 预览不可用：" + ex.Message;
            LoadingPanel.Visibility = Visibility.Visible;
            LogService.Warn("无法初始化 3D 预览：" + ex.Message);
        }
        finally
        {
            if (version == _rendererVersion) _initializing = false;
        }
    }

    private void PlayerModel3DView_Unloaded(object sender, RoutedEventArgs e)
    {
        ResetRenderer();
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_browser is not { } browser ||
            !ReferenceEquals(sender, browser.CoreWebView2))
            return;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeNode)
                ? typeNode.GetString()
                : null;
            if (type == "ready")
            {
                _webReady = true;
                BrowserHost.Visibility = Visibility.Visible;
                browser.Visibility = Visibility.Visible;
                LoadingPanel.Visibility = Visibility.Collapsed;
                SendAppearance();
                _ = browser.ExecuteScriptAsync(
                    "window.resizeSkinView && window.resizeSkinView();");
            }
            else if (type == "error")
            {
                var message = root.TryGetProperty("message", out var messageNode)
                    ? messageNode.GetString()
                    : "未知错误";
                LoadingText.Text = "3D 预览初始化失败：" + message;
                LoadingPanel.Visibility = Visibility.Visible;
                browser.Visibility = Visibility.Collapsed;
                LogService.Warn("skinview3d 初始化失败：" + message);
            }
            else if (type == "wheel" &&
                     root.TryGetProperty("delta", out var deltaNode) &&
                     deltaNode.TryGetInt32(out var delta))
            {
                WheelScrolled?.Invoke(this, delta);
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("3D 预览消息解析失败：" + ex.Message);
        }
    }

    private void SendAppearance()
    {
        if (!_webReady || _browser?.CoreWebView2 is not { } coreWebView) return;
        var payload = new
        {
            skin = ToDataUrl(_skinPath),
            cape = ToDataUrl(_capePath),
            model = _slim ? "slim" : "default"
        };
        try
        {
            coreWebView.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Closing the customization window can release the WebView before the queued update.
            _webReady = false;
        }
    }

    private string? ToDataUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        if (_dataUrls.TryGetValue(path, out var cached)) return cached;
        try
        {
            var data = "data:image/png;base64," +
                       Convert.ToBase64String(File.ReadAllBytes(path));
            _dataUrls[path] = data;
            return data;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildHtml()
    {
        using var stream = typeof(PlayerModel3DView).Assembly.GetManifestResourceStream(
            "Kumo.Assets.skinview3d.bundle.js")
            ?? throw new InvalidOperationException("skinview3d 资源缺失。");
        using var reader = new StreamReader(stream);
        var bundle = reader.ReadToEnd().Replace("</script", "<\\/script",
            StringComparison.OrdinalIgnoreCase);
        return """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<style>
html,body{width:100%;height:100%;margin:0;background:transparent;overflow:hidden}
#stage{position:relative;width:100%;height:100%;background:transparent}
#stage:after{content:"";position:absolute;left:22%;right:22%;bottom:8%;height:7%;
  border-radius:50%;background:radial-gradient(ellipse,rgba(0,0,0,.24),rgba(0,0,0,0));pointer-events:none}
canvas{position:relative;z-index:1;display:block;width:100%;height:100%;cursor:grab}
canvas:active{cursor:grabbing}
</style>
</head>
<body>
<div id="stage"><canvas id="skin"></canvas></div>
<script>
""" + bundle + """
</script>
<script>
try {
const canvas = document.getElementById("skin");
const viewer = new skinview3d.SkinViewer({
  canvas, width: Math.max(1, canvas.clientWidth), height: Math.max(1, canvas.clientHeight),
  fov: 42, zoom: 0.86, background: null, enableControls: true
});
viewer.animation = new skinview3d.WalkingAnimation();
viewer.animation.speed = 1;
viewer.autoRotate = false;
viewer.controls.enablePan = false;
viewer.controls.enableZoom = false;
viewer.controls.enableDamping = true;
viewer.cameraLight.intensity = 0.65;
viewer.globalLight.intensity = 2.25;
viewer.playerWrapper.rotation.y = Math.PI;
canvas.addEventListener("wheel", event => {
  event.preventDefault();
  window.chrome.webview.postMessage({
    type: "wheel",
    delta: Math.max(-360, Math.min(360, -Math.round(event.deltaY)))
  });
}, { passive: false });

function resize() {
  viewer.setSize(Math.max(1, canvas.clientWidth), Math.max(1, canvas.clientHeight));
  viewer.adjustCameraDistance();
}
window.resizeSkinView = resize;
new ResizeObserver(resize).observe(canvas);
window.resetSkinView = () => {
  viewer.playerWrapper.rotation.y = Math.PI;
  viewer.resetCameraPose();
  viewer.autoRotate = false;
};

let revision = 0;
window.chrome.webview.addEventListener("message", async event => {
  const data = event.data;
  const current = ++revision;
  try {
    if (data.skin) {
      await viewer.loadSkin(data.skin, { model: data.model === "slim" ? "slim" : "default" });
    } else {
      viewer.resetSkin();
    }
    if (current !== revision) return;
    if (data.cape) await viewer.loadCape(data.cape);
    else viewer.loadCape(null);
  } catch (error) {
    console.error(error);
  }
});
window.chrome.webview.postMessage({ type: "ready" });
} catch (error) {
  window.chrome.webview.postMessage({
    type: "error",
    message: error && error.message ? error.message : String(error)
  });
}
</script>
</body>
</html>
""";
    }
}
