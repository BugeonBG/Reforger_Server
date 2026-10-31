using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using RSM.Core;

namespace RSM.App;

/// <summary>Dialogs, clipboard and shell for the view model.</summary>
public sealed class WpfUiServices : IUiServices
{
    private const string Caption = "리포저 서버 관리자";
    private const int ErrorCancelled = 1223; // the user said no to the UAC prompt

    private readonly Window _owner;

    public WpfUiServices(Window owner) => _owner = owner;

    public string? PickFolder(string title, string? initialDir)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
            dlg.InitialDirectory = initialDir;
        return dlg.ShowDialog(_owner) == true ? dlg.FolderName : null;
    }

    public string? PickOpenFile(string title, string filter, string? initialPath)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        SetInitial(dlg, initialPath);
        return dlg.ShowDialog(_owner) == true ? dlg.FileName : null;
    }

    public string? PickSaveFile(string title, string filter, string? initialPath)
    {
        var dlg = new SaveFileDialog { Title = title, Filter = filter, DefaultExt = ".json", AddExtension = true };
        SetInitial(dlg, initialPath);
        return dlg.ShowDialog(_owner) == true ? dlg.FileName : null;
    }

    private static void SetInitial(FileDialog dlg, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            dlg.InitialDirectory = dir;
        dlg.FileName = Path.GetFileName(path);
    }

    /// <summary>Only the published single-file RSM.exe can replace itself; a debug build (exe + dlls)
    /// or "dotnet run" cannot.</summary>
    public string? AppExePath =>
        string.IsNullOrEmpty(typeof(WpfUiServices).Assembly.Location) && Environment.ProcessPath is { } p &&
        Path.GetFileName(p).EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        !Path.GetFileName(p).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? p
            : null;

    public void RestartApp(string exePath, string args)
    {
        Process.Start(new ProcessStartInfo(exePath, args)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
        });
        Application.Current.Shutdown();
    }

    public void ShowInfo(string message) =>
        MessageBox.Show(_owner, message, Caption, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string message) =>
        MessageBox.Show(_owner, message, Caption, MessageBoxButton.OK, MessageBoxImage.Warning);

    public bool Confirm(string message) =>
        MessageBox.Show(_owner, message, Caption, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public bool? ConfirmYesNoCancel(string message) =>
        MessageBox.Show(_owner, message, Caption, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

    public string? GetClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (ExternalException)
        {
            return null; // another program is holding the clipboard
        }
    }

    public void SetClipboardText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            ShowError("클립보드를 다른 프로그램이 사용 중입니다. 잠시 후 다시 시도하세요.");
        }
    }

    public void Open(string pathOrUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            ShowError($"열지 못했습니다.\n{pathOrUrl}\n\n{ex.Message}");
        }
    }

    public bool RunElevated(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p == null)
                return false;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }
}
