namespace RSM.Core;

// "설명" panel beside the console: explains the selected line, or (nothing selected) diagnoses this run.
public sealed partial class MainViewModel
{
    public RelayCommand RefreshLogSummaryCommand { get; }

    private bool _showLogHelp;
    public bool ShowLogHelp
    {
        get => _showLogHelp;
        set
        {
            if (Set(ref _showLogHelp, value) && value)
                RefreshLogSummary();
        }
    }

    private string? _selectedLogLine;
    /// <summary>The console line the user clicked (the window sets it).</summary>
    public string? SelectedLogLine
    {
        get => _selectedLogLine;
        set
        {
            if (!Set(ref _selectedLogLine, value))
                return;
            SelectedLogExplanation = value == null ? null : LogExplainer.Explain(value) ?? OwnMessage;
            base.OnPropertyChanged(nameof(HasSelectedLogLine));
            if (value == null)
                RefreshLogSummary();
        }
    }

    public bool HasSelectedLogLine => _selectedLogLine != null;

    private static readonly LogExplanation OwnMessage = new(LogSeverity.Info, "RSM 메시지",
        "이 프로그램(RSM)이 남긴 한국어 메시지입니다. 내용 그대로 읽으면 됩니다.", "", "RSM");

    private LogExplanation? _selectedLogExplanation;
    public LogExplanation? SelectedLogExplanation { get => _selectedLogExplanation; private set => Set(ref _selectedLogExplanation, value); }

    private LogSummary _logSummary = LogExplainer.Summarize(Array.Empty<string>());
    public LogSummary LogSummary { get => _logSummary; private set => Set(ref _logSummary, value); }

    public void RefreshLogSummary()
    {
        List<string> lines;
        lock (_logLock)
            lines = LogLines.ToList();
        LogSummary = LogExplainer.Summarize(lines);
    }
}
