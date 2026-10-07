using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FindCopy.Core;
using Microsoft.Win32;

namespace FindCopy.App;

public partial class MainWindow : Window
{
    private ScanController? _controller;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private ScanResult? _result;
    private List<GroupVM> _groups = new();
    private AppSettings _settings = AppSettings.Load();

    public MainWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => RefreshProgress();
        FolderBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    // ------------------------------------------------------------ folder selection

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Выберите папку (можно несколько)", Multiselect = true };
        var first = ParseRoots().FirstOrDefault();
        if (first != null && Directory.Exists(first)) dlg.InitialDirectory = first;
        if (dlg.ShowDialog(this) == true)
            FolderBox.Text = string.Join("; ", dlg.FolderNames);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] items)
        {
            var dirs = items.Select(p => Directory.Exists(p) ? p : Path.GetDirectoryName(p)).Where(p => p != null).Distinct().ToList();
            if (dirs.Count > 0) FolderBox.Text = string.Join("; ", dirs);
        }
    }

    private void OnFolderKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SearchButton.IsEnabled) OnSearch(sender, e);
    }

    private List<string> ParseRoots() =>
        FolderBox.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Trim('"')).Where(p => p.Length > 0).ToList();

    private void OnCloudChecked(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Файлы, которые хранятся только в облаке, будут скачаны для сравнения.\n\n" +
            "Это может занять много времени, израсходовать интернет-трафик и место на диске.\n\nВключить?",
            "Облачные файлы", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) CloudBox.IsChecked = false;
    }

    // ------------------------------------------------------------ scan

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        var roots = ParseRoots();
        if (roots.Count == 0)
        {
            MessageBox.Show(this, "Укажите папку для поиска.", "FindCopy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var missing = roots.Where(r => !Directory.Exists(r)).ToList();
        if (missing.Count > 0)
        {
            MessageBox.Show(this, "Папка не найдена:\n" + string.Join("\n", missing), "FindCopy", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var options = new ScanOptions
        {
            Roots = roots,
            Recursive = RecursiveBox.IsChecked == true,
            SkipSystem = SkipSystemBox.IsChecked == true,
            SkipHidden = SkipHiddenBox.IsChecked == true,
            ExactVerification = ExactBox.IsChecked == true,
            IncludeOnlineOnlyFiles = CloudBox.IsChecked == true,
            FollowDirectoryReparsePoints = FollowLinksBox.IsChecked == true,
            CachePath = CacheBox.IsChecked == true ? ScanCache.DefaultPath() : null,
            CompareAlternateStreams = AdsBox.IsChecked == true,
            EnumerationBackend = _settings.Backend,
            Tuning = _settings.ToTuning(),
        };

        ClearResults();
        SetBusy(true);
        _cts = new CancellationTokenSource();
        _controller = new ScanController();
        _clock.Restart();
        _timer.Start();
        try
        {
            var task = _controller.RunAsync(options, _cts.Token);
            _result = await task;
            ShowResult(_result);
        }
        catch (OperationCanceledException)
        {
            PhaseText.Text = "Поиск отменён";
            SummaryText.Text = "Поиск остановлен пользователем. Результаты неполные и не показываются.";
        }
        catch (Exception ex)
        {
            PhaseText.Text = "Ошибка";
            MessageBox.Show(this, "Поиск прерван из-за ошибки:\n\n" + ex.Message, "FindCopy", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _timer.Stop();
            _clock.Stop();
            RefreshProgress(final: true);
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_cts == null) return;
        _cts.Cancel();
        PhaseText.Text = "Отмена…";
        CancelButton.IsEnabled = false;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e) => _cts?.Cancel();

    private void SetBusy(bool busy)
    {
        SearchButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        FolderBox.IsEnabled = !busy;
        RecursiveBox.IsEnabled = !busy;
        Progress.IsIndeterminate = false;
        if (!busy) Progress.Value = _result != null ? 1 : 0;
        ExportButton.IsEnabled = !busy && _result != null && _result.Groups.Count > 0;
        SelectionBar.IsEnabled = !busy;
        ResultTree.IsEnabled = !busy;
        ExpandButton.IsEnabled = !busy && _groups.Count > 0;
    }

    private void ClearResults()
    {
        _result = null;
        SelectionBar.Visibility = Visibility.Collapsed;
        _groups = new List<GroupVM>();
        ResultTree.ItemsSource = null;
        ZeroList.ItemsSource = null;
        IssuesGrid.ItemsSource = null;
        StatsBox.Text = "";
        SummaryText.Text = "";
        DupTab.Header = "Дубликаты";
        ZeroTab.Header = "Пустые файлы";
        IssuesTab.Header = "Пропущено и ошибки";
        EmptyHint.Text = "Идёт поиск…";
        EmptyHint.Visibility = Visibility.Visible;
        Tabs.SelectedItem = DupTab;
    }

    private void RefreshProgress(bool final = false)
    {
        var c = _controller?.Counters;
        if (c == null) return;
        ElapsedText.Text = _clock.Elapsed.ToString(@"hh\:mm\:ss");
        if (!final && _cts != null && !_cts.IsCancellationRequested) PhaseText.Text = c.Phase + "…";

        long total = Interlocked.Read(ref c.StageTotal), done = Interlocked.Read(ref c.StageDone);
        if (!final)
        {
            Progress.IsIndeterminate = total <= 0;
            if (total > 0) Progress.Value = Math.Clamp((double)done / total, 0, 1);
        }

        CountersText.Text =
            $"Папок: {Fmt.Num(c.DirectoriesScanned)}   ·   файлов: {Fmt.Num(c.FilesDiscovered)} ({Fmt.Size(c.LogicalBytesDiscovered)})   ·   " +
            $"отсеяно по размеру: {Fmt.Num(c.UniqueSizeFilesRejected)}   ·   прочитано: {Fmt.Size(c.ContentBytesRead)}   ·   " +
            (c.CacheHits > 0 ? $"из кеша: {Fmt.Num(c.CacheHits)}   ·   " : "") +
            $"пропущено: {Fmt.Num(c.SkippedFiles)}   ·   ошибок: {Fmt.Num(c.ErrorFiles + c.ChangedFiles)}";
    }

    private void ShowResult(ScanResult r)
    {
        bool expand = r.Groups.Count <= 200;
        _groups = r.Groups.Select(g => MakeGroup(g, expand)).ToList();
        ResultTree.ItemsSource = _groups;
        SelectionBar.Visibility = _groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
        ExpandButton.Content = expand ? "Свернуть все" : "Развернуть все";
        DupTab.Header = $"Дубликаты ({Fmt.Num(r.Groups.Count)})";

        ZeroList.ItemsSource = r.ZeroByteFiles;
        ZeroTab.Header = $"Пустые файлы ({Fmt.Num(r.ZeroByteFiles.Count)})";
        OnShowZeroChanged(this, new RoutedEventArgs());

        IssuesGrid.ItemsSource = r.Issues.Select(i => new IssueVM(i)).ToList();
        long issueTotal = r.IssueCounts.Values.Sum();
        IssuesTab.Header = $"Пропущено и ошибки ({Fmt.Num(issueTotal)})";

        PhaseText.Text = "Поиск завершён";
        StatsBox.Text = BuildStats(r);

        long redundant = r.Groups.Sum(g => (long)g.UniquePhysicalFileCount - 1);
        if (r.Groups.Count == 0)
        {
            string msg = r.HasUncheckedFiles
                ? "Дубликаты не найдены среди успешно проверенных файлов. Часть файлов проверить не удалось — см. вкладку «Пропущено и ошибки»."
                : "Дубликаты не найдены.";
            EmptyHint.Text = msg;
            SummaryText.Text = msg;
            EmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyHint.Visibility = Visibility.Collapsed;
            SummaryText.Text =
                $"Найдено групп: {Fmt.Num(r.Groups.Count)}, лишних копий: {Fmt.Num(redundant)}. " +
                $"Можно освободить примерно {Fmt.Size(r.TotalReclaimableDisk)}." +
                (r.HasUncheckedFiles ? "  Часть файлов не проверена — см. «Пропущено и ошибки»." : "");
        }
    }

    private static string BuildStats(ScanResult r)
    {
        var c = r.Counters;
        var sb = new StringBuilder();
        void L(string k, string v) => sb.AppendLine($"{k,-46}{v}");
        L("Время поиска:", r.Elapsed.ToString(@"hh\:mm\:ss\.f"));
        L("Просмотрено папок:", Fmt.Num(c.DirectoriesScanned));
        L("Найдено файлов:", Fmt.Num(c.FilesDiscovered));
        L("Общий объём файлов:", Fmt.Size(c.LogicalBytesDiscovered));
        L("Отсеяно по уникальному размеру (не читались):", Fmt.Num(c.UniqueSizeFilesRejected));
        L("Жёстких ссылок обнаружено:", Fmt.Num(c.HardlinkAliasesDetected));
        L("Быстрых проверок (выборки XXH3):", $"{Fmt.Num(c.QuickHashFiles)}, прочитано {Fmt.Size(c.QuickHashBytesRead)}");
        L("Полных хешей BLAKE3:", $"{Fmt.Num(c.FullHashFiles)}, прочитано {Fmt.Size(c.FullHashBytesRead)}");
        if (c.ExactCompareBytesRead > 0) L("Побайтовая проверка:", $"прочитано {Fmt.Size(c.ExactCompareBytesRead)}");
        L("Групп с совпадающим хешем:", Fmt.Num(c.HashMatchGroups));
        if (c.ExactCompareBytesRead > 0 || c.ExactMatchGroups > 0) L("Групп, проверенных побайтно:", Fmt.Num(c.ExactMatchGroups));
        L("Пустых файлов (0 байт):", Fmt.Num(c.ZeroByteFiles));
        L("Пропущено системных файлов:", Fmt.Num(c.SystemSkipped));
        L("Пропущено всего (политика):", Fmt.Num(c.SkippedFiles));
        L("Ошибок доступа / чтения:", Fmt.Num(c.ErrorFiles));
        L("Изменились во время поиска:", Fmt.Num(c.ChangedFiles));
        L("Коэффициент чтения (read amplification):", c.ReadAmplification.ToString("0.0000"));
        if (c.CacheHits + c.CacheMisses > 0 || c.CacheWrites > 0)
        {
            long lookups = c.CacheHits + c.CacheMisses;
            L("Кеш: найдено / не найдено:", $"{Fmt.Num(c.CacheHits)} / {Fmt.Num(c.CacheMisses)}" + (lookups > 0 ? $" ({100.0 * c.CacheHits / lookups:0}%)" : ""));
            L("Кеш: записано:", Fmt.Num(c.CacheWrites));
        }
        if (c.UsnVolumesTracked > 0 || c.UsnUnavailable > 0)
            L("USN Journal: томов отслежено / недоступно:", $"{Fmt.Num(c.UsnVolumesTracked)} / {Fmt.Num(c.UsnUnavailable)}, сброшено записей кеша: {Fmt.Num(c.UsnInvalidated)}");
        if (c.InventoryDirectoriesReused > 0 || c.InventoryRootsRebuilt > 0)
        {
            L("Каталогов / записей из снимка USN:", $"{Fmt.Num(c.InventoryDirectoriesReused)} / {Fmt.Num(c.InventoryEntriesReused)}");
            L("Корней без пригодного снимка USN:", Fmt.Num(c.InventoryRootsRebuilt));
        }
        if (c.CacheNote != null) L("Кеш:", c.CacheNote);
        if (c.FastEnumeratedDirectories + c.FallbackEnumeratedDirectories > 0)
            L("Обход папок: быстрый / обычный:", $"{Fmt.Num(c.FastEnumeratedDirectories)} / {Fmt.Num(c.FallbackEnumeratedDirectories)}");
        if (c.AlternateStreamFiles > 0)
            L("Альтернативные потоки: файлов / прочитано:", $"{Fmt.Num(c.AlternateStreamFiles)} / {Fmt.Size(c.AlternateStreamBytesRead)}");
        if (c.AutotuneNote != null) L("Автоподбор потоков:", c.AutotuneNote);
        if (r.PhaseTimes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Время по этапам:");
            foreach (var (phase, t) in r.PhaseTimes) sb.AppendLine($"  {phase,-44}{t.TotalSeconds,8:0.00} с");
        }
        sb.AppendLine();
        sb.AppendLine("Накопители:");
        foreach (var s in r.Storage) sb.AppendLine($"  {s.Description,-22} {s.DomainKey}");
        if (r.IssueCounts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Проблемы по типам:");
            foreach (var (k, v) in r.IssueCounts.OrderByDescending(x => x.Value))
                sb.AppendLine($"  {FileStatusText.ToCode(k),-30} {Fmt.Num(v)}");
        }
        return sb.ToString();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _settings = dlg.Settings;
            _settings.Save();
        }
    }

    private void OnClearCache(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        try
        {
            ScanCache.Delete(ScanCache.DefaultPath());
            MessageBox.Show(this, "Кеш очищен.", "FindCopy", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось очистить кеш:\n" + ex.Message, "FindCopy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnShowZeroChanged(object sender, RoutedEventArgs e)
    {
        if (ZeroTab == null) return;
        ZeroTab.Visibility = ShowZeroBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (ZeroTab.Visibility != Visibility.Visible && Tabs.SelectedItem == ZeroTab) Tabs.SelectedItem = DupTab;
    }

    private void OnExpandAll(object sender, RoutedEventArgs e)
    {
        bool anyCollapsed = _groups.Any(g => !g.IsExpanded);
        foreach (var g in _groups) g.IsExpanded = anyCollapsed;
        ExpandButton.Content = anyCollapsed ? "Свернуть все" : "Развернуть все";
    }

    // ------------------------------------------------------------ selection and deletion

    private GroupVM MakeGroup(DuplicateGroup g, bool expand) => new(g, expand, UpdateSelection, OnSelectionRefused);

    private void OnSelectionRefused() =>
        MessageBox.Show(this, "В каждой группе должна остаться хотя бы одна копия.\nВсе копии сразу отметить нельзя.",
            "FindCopy", MessageBoxButton.OK, MessageBoxImage.Information);

    private IEnumerable<FileVM> CheckedFiles => _groups.SelectMany(g => g.Files).Where(f => f.IsChecked);

    private void UpdateSelection()
    {
        var files = CheckedFiles.ToList();
        long bytes = files.Sum(f => f.File.EstimatedReclaimableDiskBytes);
        SelectionText.Text = files.Count == 0 ? "Ничего не отмечено"
            : $"Отмечено: {Fmt.Num(files.Count)} {Fmt.Plural(files.Count, "файл", "файла", "файлов")}, {Fmt.Size(bytes)}";
        DeleteButton.IsEnabled = files.Count > 0;
    }

    private void OnSelectExtras(object sender, RoutedEventArgs e)
    {
        int rule = KeepRuleBox.SelectedIndex;
        foreach (var g in _groups)
        {
            FileVM keep = rule switch
            {
                1 => g.Files.OrderByDescending(f => f.File.LastWriteUtc).First(),
                2 => g.Files.OrderBy(f => f.Path.Length).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).First(),
                3 => g.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).First(),
                _ => g.Files.OrderBy(f => f.File.LastWriteUtc).ThenBy(f => f.Path.Length).First(),
            };
            keep.SetChecked(false, notifyGroup: false);
            foreach (var f in g.Files.Where(f => f != keep)) f.SetChecked(true, notifyGroup: false);
            g.OnSelectionChanged();
        }
        UpdateSelection();
    }

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        foreach (var g in _groups)
        {
            foreach (var f in g.Files) f.SetChecked(false, notifyGroup: false);
            g.OnSelectionChanged();
        }
        UpdateSelection();
    }

    private async void OnDeleteSelected(object sender, RoutedEventArgs e)
    {
        if (_cts != null || _result == null) return;
        var requests = _groups.Where(g => g.SelectedCount > 0)
            .Select(g => new DeleteRequest(g.Group, g.Files.Where(f => f.IsChecked).Select(f => f.File).ToList())).ToList();
        if (requests.Count == 0) return;
        if (requests.Any(r => r.ToDelete.Count >= r.Group.Files.Count))
        {
            OnSelectionRefused();
            return;
        }

        var deleter = new DuplicateDeleter();
        bool permanent = PermanentBox.IsChecked == true;
        var noBin = permanent ? new HashSet<string>() : new HashSet<string>(deleter.PathsWithoutRecycleBin(requests), StringComparer.OrdinalIgnoreCase);
        int count = requests.Sum(r => r.ToDelete.Count);
        int aliases = requests.Sum(r => r.ToDelete.Sum(f => f.HardLinkAliasCount));
        long bytes = requests.Sum(r => r.ToDelete.Sum(f => f.EstimatedReclaimableDiskBytes));

        if (noBin.Count > 0)
        {
            var answer = MessageBox.Show(this,
                $"{Fmt.Num(noBin.Count)} из отмеченных файлов лежат на дисках без корзины (флешка, карта памяти или сетевая папка).\n\n" +
                "Их можно удалить только безвозвратно.\n\n" +
                "Да — удалить их безвозвратно, остальные переместить в корзину.\nНет — эти файлы не трогать.\nОтмена — ничего не удалять.",
                "Удаление", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.No)
            {
                requests = requests.Select(r => new DeleteRequest(r.Group, r.ToDelete.Where(f => !noBin.Contains(f.Path)).ToList()))
                    .Where(r => r.ToDelete.Count > 0).ToList();
                if (requests.Count == 0) return;
                count = requests.Sum(r => r.ToDelete.Count);
                aliases = requests.Sum(r => r.ToDelete.Sum(f => f.HardLinkAliasCount));
                bytes = requests.Sum(r => r.ToDelete.Sum(f => f.EstimatedReclaimableDiskBytes));
                noBin.Clear();
            }
        }

        string where = permanent ? "Файлы будут удалены БЕЗВОЗВРАТНО, мимо корзины."
            : noBin.Count > 0 ? $"Файлы будут перемещены в корзину, кроме {Fmt.Num(noBin.Count)} с дисков без корзины — они будут удалены безвозвратно."
            : "Файлы будут перемещены в корзину.";
        string msg = $"Удалить {Fmt.Num(count)} {Fmt.Plural(count, "файл", "файла", "файлов")} ({Fmt.Size(bytes)})?\n\n{where}\n\n" +
                     (aliases > 0 ? $"Вместе с ними будут удалены их жёсткие ссылки: {Fmt.Num(aliases)} (иначе место не освободится).\n\n" : "") +
                     "Перед удалением каждый файл заново побайтно сверяется с копией, которая остаётся. " +
                     "Если что-то не совпадает или изменилось после поиска, файл не удаляется.";
        if (MessageBox.Show(this, msg, "Подтвердите удаление", MessageBoxButton.YesNo,
                permanent || noBin.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        _cts = new CancellationTokenSource();
        PhaseText.Text = "Удаление…";
        Progress.IsIndeterminate = false;
        Progress.Value = 0;
        var progress = new Progress<(int Done, int Total, string Path)>(p =>
        {
            Progress.Value = p.Total == 0 ? 1 : (double)p.Done / p.Total;
            CountersText.Text = $"Удаление {p.Done} из {p.Total}: {p.Path}";
        });
        var bin = noBin;
        List<DeleteOutcome> outcomes;
        try
        {
            var token = _cts.Token;
            outcomes = await Task.Run(() => deleter.Run(requests,
                path => permanent || bin.Contains(path) ? DeleteMode.Permanent : DeleteMode.RecycleBin, progress, token));
        }
        catch (OperationCanceledException)
        {
            outcomes = new List<DeleteOutcome>();
            PhaseText.Text = "Удаление остановлено";
            MessageBox.Show(this, "Удаление остановлено. Уже удалённые файлы удалены, остальные не тронуты. Запустите поиск заново, чтобы обновить список.",
                "FindCopy", MessageBoxButton.OK, MessageBoxImage.Information);
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
            return;
        }
        _cts.Dispose();
        _cts = null;
        ApplyDeletion(outcomes);
        SetBusy(false);
    }

    private void ApplyDeletion(List<DeleteOutcome> outcomes)
    {
        var deleted = new HashSet<string>(outcomes.Where(o => o.Deleted).Select(o => o.Path), StringComparer.OrdinalIgnoreCase);
        var newGroups = new List<DuplicateGroup>();
        foreach (var g in _result!.Groups)
        {
            var remaining = g.Files.Where(f => !deleted.Contains(f.Path)).ToList();
            if (remaining.Count == g.Files.Count) { newGroups.Add(g); continue; }
            if (remaining.Count >= 2)
                newGroups.Add(new DuplicateGroup { GroupId = g.GroupId, LogicalSize = g.LogicalSize, Hash = g.Hash, Verification = g.Verification, Files = remaining });
        }
        _result = new ScanResult
        {
            Groups = newGroups, ZeroByteFiles = _result.ZeroByteFiles, ZeroByteGroups = _result.ZeroByteGroups, Counters = _result.Counters, Issues = _result.Issues,
            IssueCounts = _result.IssueCounts, Storage = _result.Storage, Elapsed = _result.Elapsed, PhaseTimes = _result.PhaseTimes,
        };

        // Keep selection of files that were not deleted.
        var stillChecked = new HashSet<string>(CheckedFiles.Where(f => !deleted.Contains(f.Path)).Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var expanded = new HashSet<int>(_groups.Where(g => g.IsExpanded).Select(g => g.Group.GroupId));
        _groups = newGroups.Select(g => MakeGroup(g, expanded.Contains(g.GroupId))).ToList();
        foreach (var f in _groups.SelectMany(g => g.Files).Where(f => stillChecked.Contains(f.Path))) f.SetChecked(true, notifyGroup: false);
        foreach (var g in _groups) g.OnSelectionChanged();
        ResultTree.ItemsSource = _groups;
        DupTab.Header = $"Дубликаты ({Fmt.Num(_groups.Count)})";
        SelectionBar.Visibility = _groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_groups.Count == 0) EmptyHint.Text = "Дубликатов больше нет.";
        UpdateSelection();

        var failed = outcomes.Where(o => !o.Deleted).ToList();
        var notes = outcomes.Where(o => o.Deleted && o.Reason != null).ToList();
        if (failed.Count > 0 || notes.Count > 0)
        {
            var issues = (IssuesGrid.ItemsSource as IEnumerable<IssueVM>)?.ToList() ?? new List<IssueVM>();
            issues.InsertRange(0, failed.Select(o => new IssueVM(o.Path, "Не удалён", o.Reason)));
            issues.InsertRange(0, notes.Select(o => new IssueVM(o.Path, "Удалён с замечанием", o.Reason)));
            IssuesGrid.ItemsSource = issues;
            IssuesTab.Header = $"Пропущено и ошибки ({Fmt.Num(issues.Count)})";
        }

        long freed = outcomes.Sum(o => o.FreedBytes);
        PhaseText.Text = "Удаление завершено";
        CountersText.Text = "";
        string summary = $"Удалено файлов: {Fmt.Num(deleted.Count)}, освобождено примерно {Fmt.Size(freed)}.";
        if (failed.Count > 0)
            summary += $"\n\nНе удалено: {Fmt.Num(failed.Count)}. Причины — на вкладке «Пропущено и ошибки».";
        if (notes.Count > 0)
            summary += $"\n\nФайлов с замечаниями: {Fmt.Num(notes.Count)}. Пути восстановления и оставленные ссылки — на вкладке «Пропущено и ошибки».";
        SummaryText.Text = summary.Replace("\n\n", " ");
        ShowNotification(summary, "Удаление", failed.Count > 0 || notes.Count > 0);
    }

    protected virtual void ShowNotification(string message, string title, bool warning) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OK, warning ? MessageBoxImage.Warning : MessageBoxImage.Information);

    // ------------------------------------------------------------ file actions

    private static FileVM? FileOf(object sender) => (sender as FrameworkElement)?.DataContext as FileVM;

    private void OnShowInExplorer(object sender, RoutedEventArgs e)
    {
        if (FileOf(sender) is { } f) ShowInExplorer(f.Path);
    }

    private static void ShowInExplorer(string path)
    {
        // Full path: a stray explorer.exe next to FindCopy.exe (e.g. in Downloads) must never be started instead.
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try { Process.Start(new ProcessStartInfo(explorer, $"/select,\"{path}\"") { UseShellExecute = false }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "FindCopy"); }
    }

    /// <summary>Extensions Windows runs as programs or scripts when "opened".</summary>
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".msi", ".msp", ".msix", ".appx", ".appref-ms", ".application",
        ".lnk", ".url", ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh", ".ps1", ".psm1", ".hta", ".cpl", ".reg",
        ".jar", ".py", ".pyw", ".inf", ".settingcontent-ms", ".library-ms", ".search-ms", ".scf", ".chm",
    };

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        if (FileOf(sender) is not { } f) return;
        if (RunnableExtensions.Contains(Path.GetExtension(f.Path)) &&
            MessageBox.Show(this,
                "Этот файл — программа или сценарий. «Открыть» запустит его.\n\n" + f.Path + "\n\nЗапустить?",
                "FindCopy", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try { Process.Start(new ProcessStartInfo(f.Path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "FindCopy"); }
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (FileOf(sender) is { } f)
        {
            try { Clipboard.SetText(f.Path); } catch { }
        }
    }

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultTree.SelectedItem is FileVM f) ShowInExplorer(f.Path);
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Сохранить отчёт",
            Filter = "CSV (разделитель «;»)|*.csv",
            FileName = $"FindCopy-{DateTime.Now:yyyy-MM-dd_HH-mm}.csv",
        };
        if (dlg.ShowDialog(this) != true) return;
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine("Группа;Путь;Размер (байт);Изменён;Проверка;BLAKE3;Жёсткая ссылка на");
        foreach (var g in _result.Groups)
        {
            string ver = g.Verification == VerificationState.ExactMatch ? "EXACT_MATCH" : "HASH_MATCH";
            foreach (var f in g.Files)
            {
                sb.AppendLine(string.Join(";", g.GroupId, Q(f.Path), g.LogicalSize, f.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), ver, g.Hash, ""));
                foreach (var a in f.HardLinkAliases)
                    sb.AppendLine(string.Join(";", g.GroupId, Q(a), g.LogicalSize, "", ver, g.Hash, Q(f.Path)));
            }
        }
        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось сохранить файл:\n" + ex.Message, "FindCopy", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
