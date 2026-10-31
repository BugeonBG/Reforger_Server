namespace RSM.Core;

/// <summary>What the view model needs from the window. WPF implements it in RSM.App;
/// tests implement it with fakes.</summary>
public interface IUiServices
{
    string? PickFolder(string title, string? initialDir);

    string? PickOpenFile(string title, string filter, string? initialPath);

    string? PickSaveFile(string title, string filter, string? initialPath);

    void ShowInfo(string message);

    void ShowError(string message);

    bool Confirm(string message);

    /// <summary>Yes = true, No = false, Cancel = null.</summary>
    bool? ConfirmYesNoCancel(string message);

    string? GetClipboardText();

    void SetClipboardText(string text);

    /// <summary>Opens a folder in Explorer or a URL in the browser.</summary>
    void Open(string pathOrUrl);

    /// <summary>Runs a program as administrator (UAC prompt). Returns false if the user said no.</summary>
    bool RunElevated(string exe, string args);

    /// <summary>Full path of the running RSM.exe; null when it cannot be replaced (e.g. run from a debugger).</summary>
    string? AppExePath { get; }

    /// <summary>Starts <paramref name="exePath"/> and closes this program (after a self-update).</summary>
    void RestartApp(string exePath, string args);
}
