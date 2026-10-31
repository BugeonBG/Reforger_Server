using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace RSM.App;

public partial class App : Application
{
    private static bool _showingError;

    public static string ErrorLogPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RSM", "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        WriteErrorLog(e.Exception);

        // One dialog at a time. While a message box is open, queued work keeps running; if it fails
        // the same way it would otherwise stack up dozens of identical boxes.
        if (_showingError)
            return;

        _showingError = true;
        try
        {
            MessageBox.Show(
                "예상하지 못한 오류가 발생했습니다.\n\n" + e.Exception.Message +
                "\n\n자세한 내용은 아래 파일에 저장했습니다. 문제를 알려줄 때 이 파일을 같이 보내주세요.\n" + ErrorLogPath,
                "리포저 서버 관리자",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _showingError = false;
        }
    }

    private static void WriteErrorLog(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogPath)!);
            File.AppendAllText(ErrorLogPath, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}\n\n", Encoding.UTF8);
        }
        catch (Exception)
        {
            // nothing sensible left to do
        }
    }
}
