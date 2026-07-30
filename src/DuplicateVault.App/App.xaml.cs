using System.Windows;
using DuplicateVault.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DuplicateVault.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dataRoot = ArgValue(e.Args, "--data-root") ?? AppContext.BaseDirectory;
        try
        {
            _host = Host.CreateDefaultBuilder(e.Args)
                .ConfigureServices(services =>
                {
                    services.AddDuplicateVault(dataRoot);
                    services.AddSingleton<GuiSettingsStore>();
                    services.AddSingleton<MainViewModel>();
                    services.AddSingleton<MainWindow>();
                })
                .Build();
            await _host.Services.GetRequiredService<DuplicateVault.Core.IDuplicateVaultDatabase>().InitializeAsync(CancellationToken.None);
            _host.Services.GetRequiredService<MainWindow>().Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("La directory dati dell'applicazione non è scrivibile.\nSposta l'applicazione in una directory scrivibile o specifica un'altra directory dati.\n\n" + ex.Message, "DuplicateVault", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null) await _host.StopAsync(TimeSpan.FromSeconds(2));
        _host?.Dispose();
        base.OnExit(e);
    }

    private static string? ArgValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
