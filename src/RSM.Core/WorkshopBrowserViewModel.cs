using System.Collections.ObjectModel;
using System.Globalization;

namespace RSM.Core;

public sealed class CategoryFilter : ObservableObject
{
    private bool _isChecked;

    public CategoryFilter(WorkshopCategory category) => Category = category;

    public WorkshopCategory Category { get; }
    public string Tag => Category.Tag;
    public string Label => Category.Label;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
}

/// <summary>One mod card in the browser.</summary>
public sealed class WorkshopResult : ObservableObject
{
    private bool _isAdded;

    public WorkshopResult(WorkshopListItem item, bool isAdded, Action<WorkshopResult> add, Action<WorkshopResult> open)
    {
        Item = item;
        _isAdded = isAdded;
        AddCommand = new RelayCommand(() => add(this), () => !IsAdded);
        OpenCommand = new RelayCommand(() => open(this));
    }

    public WorkshopListItem Item { get; }
    public string Id => Item.Id;
    public string Name => Item.Name.Length > 0 ? Item.Name : Item.Id;
    public string Author => Item.Author;
    public string Summary => Item.Summary.Replace('\n', ' ').Replace('\r', ' ');
    public string? ThumbnailUrl => Item.ThumbnailUrl;
    public string SizeText => FormatSize(Item.SizeBytes);
    public string SubscribersText => "구독 " + Item.Subscribers.ToString("N0", CultureInfo.InvariantCulture);
    public string RatingText => Item.Rating > 0 ? $"평점 {Math.Round(Item.Rating * 100)}%" : "";

    /// <summary>Category tags of this mod, in Korean, e.g. "무기 · 효과".</summary>
    public string CategoriesText => string.Join(" · ",
        WorkshopCatalog.Categories.Where(c => Item.Tags.Contains(c.Tag)).Select(c => c.Label));

    public bool IsAdded
    {
        get => _isAdded;
        set
        {
            if (Set(ref _isAdded, value))
                AddCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand AddCommand { get; }
    public RelayCommand OpenCommand { get; }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }
}

/// <summary>One section of results: "전체 모드", or one checked category.</summary>
public sealed class WorkshopGroup : ObservableObject
{
    private int _totalCount;
    private bool _hasMore;
    private bool _isLoading;
    private bool _isEmpty;
    private string _errorText = "";

    public WorkshopGroup(string? tag, string title)
    {
        Tag = tag;
        Title = title;
    }

    public string? Tag { get; }
    public string Title { get; }
    public ObservableCollection<WorkshopResult> Items { get; } = new();
    public int NextPage { get; set; } = 1;

    public int TotalCount { get => _totalCount; set { if (Set(ref _totalCount, value)) OnPropertyChanged(nameof(CountText)); } }
    public string CountText => TotalCount.ToString("N0", CultureInfo.InvariantCulture) + "개";
    public bool HasMore { get => _hasMore; set => Set(ref _hasMore, value); }
    public bool IsLoading { get => _isLoading; set => Set(ref _isLoading, value); }
    public bool IsEmpty { get => _isEmpty; set => Set(ref _isEmpty, value); }
    public string ErrorText { get => _errorText; set { if (Set(ref _errorText, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => ErrorText.Length > 0;

    public AsyncRelayCommand? LoadMoreCommand { get; set; }
}

/// <summary>The "워크숍 둘러보기" page: every mod at first; with categories checked, one section per category.</summary>
public sealed class WorkshopBrowserViewModel : ObservableObject
{
    private readonly WorkshopClient _client;
    private readonly Func<string, bool> _isAdded;
    private readonly Action<WorkshopListItem> _add;
    private readonly Action<string> _open;
    private readonly int _debounceMs;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _debounceCts;
    private string _searchText = "";
    private WorkshopSort _selectedSort;
    private bool _hasLoaded;

    public WorkshopBrowserViewModel(WorkshopClient client, Func<string, bool> isAdded, Action<WorkshopListItem> add, Action<string> open, int debounceMs = 450)
    {
        _client = client;
        _isAdded = isAdded;
        _add = add;
        _open = open;
        _debounceMs = debounceMs;
        _selectedSort = WorkshopCatalog.Sorts[0];

        foreach (var c in WorkshopCatalog.Categories)
        {
            var f = new CategoryFilter(c);
            f.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CategoryFilter.IsChecked))
                {
                    OnPropertyChanged(nameof(AnyCategoryChecked));
                    ClearCategoriesCommand?.RaiseCanExecuteChanged();
                    ScheduleRefresh();
                }
            };
            Categories.Add(f);
        }

        SearchCommand = new AsyncRelayCommand(RefreshAsync, null, _ => { });
        ClearCategoriesCommand = new RelayCommand(ClearCategories, () => AnyCategoryChecked);
    }

    public ObservableCollection<CategoryFilter> Categories { get; } = new();
    public ObservableCollection<WorkshopGroup> Groups { get; } = new();
    public IReadOnlyList<WorkshopSort> Sorts => WorkshopCatalog.Sorts;

    public string SearchText { get => _searchText; set => Set(ref _searchText, value ?? ""); }

    public WorkshopSort SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (value != null && Set(ref _selectedSort, value))
                ScheduleRefresh();
        }
    }

    public bool AnyCategoryChecked => Categories.Any(c => c.IsChecked);
    public bool HasLoaded { get => _hasLoaded; private set => Set(ref _hasLoaded, value); }

    public AsyncRelayCommand SearchCommand { get; }
    public RelayCommand ClearCategoriesCommand { get; }

    /// <summary>The refresh started by the last filter change (tests await it).</summary>
    public Task LastRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>Loads the first time the page is opened; does nothing after that.</summary>
    public Task EnsureLoadedAsync() => HasLoaded ? Task.CompletedTask : RefreshAsync();

    /// <summary>Starts over with the current search, sort and categories.</summary>
    public async Task RefreshAsync()
    {
        _debounceCts?.Cancel();
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        HasLoaded = true;

        Groups.Clear();
        var chosen = Categories.Where(c => c.IsChecked).ToList();
        if (chosen.Count == 0)
            Groups.Add(NewGroup(null, string.IsNullOrWhiteSpace(SearchText) ? "전체 모드" : $"'{SearchText.Trim()}' 검색 결과"));
        else
            foreach (var c in chosen)
                Groups.Add(NewGroup(c.Tag, c.Label));

        await Task.WhenAll(Groups.ToList().Select(g => LoadPageAsync(g, cts.Token)));
    }

    /// <summary>Re-marks which results are already in the server's mod list.</summary>
    public void RefreshAdded()
    {
        foreach (var g in Groups)
            foreach (var r in g.Items)
                r.IsAdded = _isAdded(r.Id);
    }

    private WorkshopGroup NewGroup(string? tag, string title)
    {
        var g = new WorkshopGroup(tag, title);
        g.LoadMoreCommand = new AsyncRelayCommand(() => LoadPageAsync(g, _loadCts?.Token ?? default), () => g.HasMore && !g.IsLoading, _ => { });
        g.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WorkshopGroup.HasMore) or nameof(WorkshopGroup.IsLoading))
                g.LoadMoreCommand.RaiseCanExecuteChanged();
        };
        return g;
    }

    private async Task LoadPageAsync(WorkshopGroup g, CancellationToken token)
    {
        g.IsLoading = true;
        g.ErrorText = "";
        try
        {
            var page = await _client.SearchAsync(SearchText, g.Tag, SelectedSort.Value, g.NextPage, token);
            if (token.IsCancellationRequested)
                return;

            g.TotalCount = page.TotalCount;
            foreach (var item in page.Items)
            {
                if (g.Items.Any(r => r.Id == item.Id))
                    continue; // the list shifted between pages
                g.Items.Add(new WorkshopResult(item, _isAdded(item.Id), r => _add(r.Item), r => _open(WorkshopPage.BaseUrl + r.Id)));
            }
            g.NextPage++;
            g.HasMore = page.Items.Count > 0 && g.Items.Count < page.TotalCount;
            g.IsEmpty = g.Items.Count == 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // a newer search replaced this one
        }
        catch (Exception ex) when (ex is HttpRequestException or FormatException or OperationCanceledException)
        {
            g.ErrorText = ex is OperationCanceledException
                ? "워크숍이 응답하지 않습니다. 잠시 후 다시 시도하세요."
                : $"워크숍 목록을 불러오지 못했습니다. 인터넷 연결을 확인하세요. ({ex.Message})";
        }
        finally
        {
            g.IsLoading = false;
        }
    }

    private void ClearCategories()
    {
        foreach (var c in Categories)
            c.IsChecked = false;
    }

    // Ticking several boxes in a row should cause one reload, not one per click.
    private void ScheduleRefresh()
    {
        if (!HasLoaded)
            return; // nothing on screen yet; the first open loads with whatever is chosen
        _debounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        LastRefresh = DebouncedRefresh(cts.Token);
    }

    private async Task DebouncedRefresh(CancellationToken token)
    {
        try
        {
            if (_debounceMs > 0)
                await Task.Delay(_debounceMs, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!token.IsCancellationRequested)
            await RefreshAsync();
    }
}
