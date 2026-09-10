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
    public static bool IsIndependentNewWindowMode { get; set; } = true;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;

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

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "Stage Manager"
        };
        
        var contextMenu = new System.Windows.Forms.ContextMenuStrip();
        
        var modeItem = new System.Windows.Forms.ToolStripMenuItem("新窗口独立分组 (Independent New Windows)")
        {
            CheckOnClick = true,
            Checked = IsIndependentNewWindowMode
        };
        modeItem.CheckedChanged += (s, args) => 
        {
            IsIndependentNewWindowMode = modeItem.Checked;
            Log.Information($"[Settings] IsIndependentNewWindowMode changed to {IsIndependentNewWindowMode}");
        };
        contextMenu.Items.Add(modeItem);
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出 (Exit)");
        exitItem.Click += (s, args) => Current.Shutdown();
        contextMenu.Items.Add(exitItem);
        
        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        Log.Information("Application shutting down...");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}

