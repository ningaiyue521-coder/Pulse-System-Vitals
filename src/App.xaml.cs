using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace DemoApp;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) =>
        WriteCrashLog(e.Exception);

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            var message = new StringBuilder()
                .AppendLine(DateTimeOffset.Now.ToString("O"))
                .AppendLine(exception.ToString())
                .AppendLine()
                .ToString();
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "Pulse.crash.log"), message);
        }
        catch
        {
        }
    }
}
