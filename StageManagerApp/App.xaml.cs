using System.Configuration;
using System.Data;
using System.Windows;
using Serilog;
using System.IO;
using System;

namespace StageManagerApp;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Async(a => a.File(
                Path.Combine(logDir, "stagemanager-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [T{ThreadId}] {Message:lj}{NewLine}{Exception}"))
            .Enrich.WithThreadId()
            .CreateLogger();
            
        Log.Information("Application starting up...");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Application shutting down...");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}

