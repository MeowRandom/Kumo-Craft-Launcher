using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Kumo.Services;

namespace Kumo;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var instanceKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.AppDataRoot)))[..16];
        _singleInstanceMutex = new Mutex(true, $@"Local\Kumo.{instanceKey}", out var isFirstInstance);
        if (!isFirstInstance)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            MessageBox.Show("库莫 Kumo Launcher 已经在运行。", "Kumo",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogService.Error("后台任务异常", args.Exception);
            args.SetObserved();
        };
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            var path = Path.Combine(AppPaths.AppDataRoot, "startup-error.log");
            try
            {
                Directory.CreateDirectory(AppPaths.AppDataRoot);
                File.WriteAllText(path, ex.ToString());
            }
            catch
            {
                // The dialog below still provides the actionable message.
            }
            MessageBox.Show(ex.ToString(), "Kumo 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogService.Error("界面线程异常", e.Exception);
        try
        {
            Directory.CreateDirectory(AppPaths.AppDataRoot);
            File.WriteAllText(Path.Combine(AppPaths.AppDataRoot, "startup-error.log"), e.Exception.ToString());
        }
        catch
        {
            // Logging must not obscure the original exception.
        }
        MessageBox.Show(
            $"启动器遇到错误：\n\n{e.Exception.Message}\n\n详细信息已写入启动器日志。",
            "Kumo",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
