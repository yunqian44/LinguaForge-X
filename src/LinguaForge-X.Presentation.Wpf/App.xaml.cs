using LinguaForge_X.Application;
using LinguaForge_X.Infrastructure;
using LinguaForge_X.Presentation.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Windows;
using WpfApplication = System.Windows.Application;

namespace LinguaForge_X.Presentation.Wpf;

public partial class App : WpfApplication
{
    private readonly IHost _host;

    public App()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services
                    .AddApplication()
                    .AddInfrastructure()
                    .AddSingleton<MainViewModel>()
                    .AddSingleton<MainWindow>();
            })
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            await _host.StartAsync();
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception)
        {
            MessageBox.Show("应用启动失败，请检查本地安装和配置文件。", "LinguaForge-X", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); }
        finally
        {
            _host.Dispose();
            base.OnExit(e);
        }
    }
}
