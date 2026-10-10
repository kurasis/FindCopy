using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using FindCopy.Core;

namespace FindCopy.App;

public static class Fmt
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Size(long bytes)
    {
        if (bytes < 0) return "—";
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} Б" : v.ToString(v >= 100 ? "0" : v >= 10 ? "0.#" : "0.##", Ru) + " " + units[u];
    }

    public static string Num(long n) => n.ToString("#,0", Ru);

    /// <summary>Russian plural: 1 файл, 2 файла, 5 файлов.</summary>
    public static string Plural(long n, string one, string few, string many)
    {
        long m10 = n % 10, m100 = n % 100;
        if (m10 == 1 && m100 != 11) return one;
        if (m10 is >= 2 and <= 4 && (m100 < 12 || m100 > 14)) return few;
        return many;
    }

    public static string Status(FileStatus s) => s switch
    {
        FileStatus.AccessDenied => "Нет доступа",
        FileStatus.FileNotFoundDuringScan => "Файл исчез во время поиска",
        FileStatus.SharingViolation => "Файл занят другой программой",
        FileStatus.IoError => "Ошибка чтения",
        FileStatus.ChangedDuringScan => "Изменился во время поиска",
        FileStatus.PolicySkipped => "Исключён настройками",
        FileStatus.CloudContentNotLocal => "Только в облаке (не скачан)",
        FileStatus.ReparseSkipped => "Ссылка (не обходится)",
        FileStatus.Unsupported => "Не поддерживается",
        FileStatus.Cancelled => "Отменено",
        _ => s.ToString(),
    };
}

public sealed class GroupVM : INotifyPropertyChanged
{
    private static readonly Brush HashBg = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0xF6, 0xFF)));
    private static readonly Brush HashFg = Freeze(new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8)));
    private static readonly Brush ExactBg = Freeze(new SolidColorBrush(Color.FromRgb(0xEC, 0xFD, 0xF5)));
    private static readonly Brush ExactFg = Freeze(new SolidColorBrush(Color.FromRgb(0x04, 0x78, 0x57)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private bool _expanded;

    public GroupVM(DuplicateGroup g, bool expanded, Action? selectionChanged = null, Action? refused = null)
    {
        Group = g;
        _expanded = expanded;
        _selectionChanged = selectionChanged;
        _refused = refused;
        Number = "#" + g.GroupId;
        int copies = g.UniquePhysicalFileCount;
        Title = $"{copies} {Fmt.Plural(copies, "копия", "копии", "копий")} по {Fmt.Size(g.LogicalSize)}";
        if (g.FileCount > copies)
            Title += $" ({g.FileCount} {Fmt.Plural(g.FileCount, "путь", "пути", "путей")} с учётом жёстких ссылок)";
        Reclaim = $"можно освободить ~{Fmt.Size(g.EstimatedReclaimableDiskBytes)}";
        bool exact = g.Verification == VerificationState.ExactMatch;
        Badge = exact ? "Проверено побайтно" : "Совпадает хеш BLAKE3";
        BadgeBackground = exact ? ExactBg : HashBg;
        BadgeForeground = exact ? ExactFg : HashFg;
        Files = g.Files.Select(f => new FileVM(f, this)).ToList();
    }

    private readonly Action? _selectionChanged;
    private readonly Action? _refused;

    public int SelectedCount => Files.Count(f => f.IsChecked);
    public string SelectionText => SelectedCount == 0 ? "" : $"отмечено {SelectedCount}";

    /// <summary>A group must keep at least one copy: the last unchecked file cannot be checked.</summary>
    internal bool CanCheckOneMore => Files.Count(f => !f.IsChecked) > 1;

    internal void OnSelectionChanged(bool notifySelection = true)
    {
        PropertyChanged?.Invoke(this, new(nameof(SelectionText)));
        if (notifySelection) _selectionChanged?.Invoke();
    }

    internal void OnRefused() => _refused?.Invoke();

    public DuplicateGroup Group { get; }
    public string Number { get; }
    public string Title { get; }
    public string Reclaim { get; }
    public bool IsExact => Group.Verification == VerificationState.ExactMatch;
    public string Badge { get; }
    public Brush BadgeBackground { get; }
    public Brush BadgeForeground { get; }
    public IReadOnlyList<FileVM> Files { get; }

    public bool IsExpanded
    {
        get => _expanded;
        set { if (_expanded != value) { _expanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class FileVM : INotifyPropertyChanged
{
    private bool _checked;

    public FileVM(DuplicateFile f, GroupVM group)
    {
        File = f;
        Group = group;
        Path = f.Path;
        FileName = System.IO.Path.GetFileName(f.Path);
        Directory = System.IO.Path.GetDirectoryName(f.Path) ?? "";
        var local = f.LastWriteUtc.ToLocalTime();
        Info = $"Изменён {local:dd.MM.yyyy HH:mm}";
        if (f.AllocatedSize >= 0 && f.AllocatedSize != f.LogicalSize)
            Info += $"  ·  на диске {Fmt.Size(f.AllocatedSize)}";
        if (f.HardLinkAliasCount > 0)
            AliasText = $"Жёсткие ссылки на этот же файл (не отдельные копии): " + string.Join(";  ", f.HardLinkAliases);
    }

    public string Path { get; }
    public string FileName { get; }
    public string Directory { get; }
    public string Info { get; }
    public string? AliasText { get; }
    public bool HasAliases => AliasText != null;
    public DuplicateFile File { get; }
    public GroupVM Group { get; }

    /// <summary>Marked for deletion.</summary>
    public bool IsChecked
    {
        get => _checked;
        set => SetChecked(value, notifyGroup: true);
    }

    internal bool SetChecked(bool value, bool notifyGroup)
    {
        if (value == _checked) return true;
        if (value && !Group.CanCheckOneMore)
        {
            PropertyChanged?.Invoke(this, new(nameof(IsChecked)));   // revert the checkbox
            if (notifyGroup) Group.OnRefused();
            return false;
        }
        _checked = value;
        PropertyChanged?.Invoke(this, new(nameof(IsChecked)));
        if (notifyGroup) Group.OnSelectionChanged();
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class IssueVM
{
    public IssueVM(ScanIssue i) { Path = i.Path; StatusText = Fmt.Status(i.Status); Message = i.Message; }
    public IssueVM(string path, string status, string? message) { Path = path; StatusText = status; Message = message; }
    public string Path { get; }
    public string StatusText { get; }
    public string? Message { get; }
}
