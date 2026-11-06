using System.IO;
using System.Windows;

namespace MultiMediaPlayer.App;
public partial class App : Application
{
    public static string[] Arguments { get; private set; } = [];
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Arguments = e.Args;
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write(args.Exception.ToString());
            MessageBox.Show("操作失败：" + args.Exception.Message, "多画面播放器", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        try { new MainWindow().Show(); }
        catch (Exception ex) { Log.Write(ex.ToString()); MessageBox.Show("无法启动播放器：" + ex.Message); Shutdown(1); }
    }
}
public static class Log
{
    private static readonly object Sync = new();
    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiMediaPlayer");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "player.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch { /* Logging must never interrupt media playback. */ }
    }
}
