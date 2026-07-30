using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using DuplicateVault.Core;
using DuplicateVault.Infrastructure;
using Microsoft.Win32;

namespace DuplicateVault.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IFileScanner _scanner;
    private readonly IHardLinkService _hardLinks;
    private readonly AppPaths _paths;
    private readonly IDuplicateVaultDatabase _database;
    private readonly GuiSettingsStore _settingsStore;
    private readonly RelayCommand _addRootCommand;
    private readonly RelayCommand _removeRootCommand;
    private readonly RelayCommand _settingsCommand;
    private readonly RelayCommand _startScanCommand;
    private readonly RelayCommand _startCleanScanCommand;
    private readonly RelayCommand _cancelScanCommand;
    private readonly RelayCommand _dryRunCommand;
    private readonly RelayCommand _executeHardLinkCommand;
    private CancellationTokenSource? _scanCts;
    private GuiSettings _settings;
    private DuplicateGroup? _selectedGroup;
    private DuplicateFile? _selectedFile;
    private ScanRootItem? _selectedRoot;
    private string _status = "Pronto";
    private string _summary = "";
    private string _groupCountText = "0";
    private string _reclaimableText = "0 byte";
    private string _includedFilesText = "0";
    private string _progressActivity = "Nessuna scansione in corso";
    private string _progressPath = "-";
    private string _progressCounters = "0 file letti, 0 inclusi, 0 hash";
    private string _progressPercentText = "";
    private double _progressPercent;
    private bool _isProgressIndeterminate;
    private bool _isScanning;
    private bool _isCancellationRequested;

    public MainViewModel(IFileScanner scanner, IHardLinkService hardLinks, AppPaths paths, GuiSettingsStore settingsStore, IDuplicateVaultDatabase database)
    {
        _scanner = scanner;
        _hardLinks = hardLinks;
        _paths = paths;
        _database = database;
        _settingsStore = settingsStore;
        _settings = _settingsStore.Load();
        SelectedMode = _settings.ScanMode;
        MinimumSizeText = _settings.MinimumSizeText;
        if (_settings.RememberScanRoots)
        {
            foreach (var root in _settings.ScanRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Roots.Add(new ScanRootItem(root));
            }
        }

        _addRootCommand = new RelayCommand(_ => AddRoot(), _ => !IsScanning);
        _removeRootCommand = new RelayCommand(_ => RemoveRoot(), _ => !IsScanning && SelectedRoot is not null);
        _settingsCommand = new RelayCommand(_ => OpenSettings(), _ => !IsScanning);
        _startScanCommand = new RelayCommand(async _ => await StartScanAsync(), _ => !IsScanning && Roots.Count > 0);
        _startCleanScanCommand = new RelayCommand(async _ => await StartScanAsync(ignoreCache: true), _ => !IsScanning && Roots.Count > 0);
        _cancelScanCommand = new RelayCommand(_ => CancelScan(), _ => IsScanning && !IsCancellationRequested);
        _dryRunCommand = new RelayCommand(async _ => await ReplaceAsync(true), _ => !IsScanning && SelectedGroup is not null && SelectedFile is not null);
        _executeHardLinkCommand = new RelayCommand(async _ => await ReplaceAsync(false), _ => !IsScanning && SelectedGroup is not null && SelectedFile is not null);
        _ = RefreshRootStatusesAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ScanRootItem> Roots { get; } = [];
    public ObservableCollection<DuplicateGroup> DuplicateGroups { get; } = [];
    public IReadOnlyList<string> Modes { get; } = ["Quick", "Full", "Strict"];
    public string RootPath { get; set; } = "";
    public ScanRootItem? SelectedRoot
    {
        get => _selectedRoot;
        set { _selectedRoot = value; OnPropertyChanged(); RefreshCommands(); }
    }
    public string SelectedMode { get; set; } = "Quick";
    public string MinimumSizeText { get; set; } = "1MiB";
    public string VersionText => "Versione " + VersionDisplay.GetCleanVersion();
    public string DataRoot => _paths.DataRoot;
    public ICommand AddRootCommand => _addRootCommand;
    public ICommand RemoveRootCommand => _removeRootCommand;
    public ICommand SettingsCommand => _settingsCommand;
    public ICommand StartScanCommand => _startScanCommand;
    public ICommand StartCleanScanCommand => _startCleanScanCommand;
    public ICommand CancelScanCommand => _cancelScanCommand;
    public ICommand DryRunCommand => _dryRunCommand;
    public ICommand ExecuteHardLinkCommand => _executeHardLinkCommand;

    public bool IsScanning
    {
        get => _isScanning;
        set { _isScanning = value; OnPropertyChanged(); RefreshCommands(); }
    }

    public bool IsCancellationRequested
    {
        get => _isCancellationRequested;
        set { _isCancellationRequested = value; OnPropertyChanged(); RefreshCommands(); }
    }

    public DuplicateGroup? SelectedGroup
    {
        get => _selectedGroup;
        set { _selectedGroup = value; OnPropertyChanged(); OnPropertyChanged(nameof(SelectedFiles)); RefreshCommands(); }
    }

    public IReadOnlyList<DuplicateFile> SelectedFiles => SelectedGroup?.Files ?? [];

    public DuplicateFile? SelectedFile
    {
        get => _selectedFile;
        set { _selectedFile = value; OnPropertyChanged(); RefreshCommands(); }
    }

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public string Summary
    {
        get => _summary;
        set { _summary = value; OnPropertyChanged(); }
    }

    public string GroupCountText
    {
        get => _groupCountText;
        set { _groupCountText = value; OnPropertyChanged(); }
    }

    public string ReclaimableText
    {
        get => _reclaimableText;
        set { _reclaimableText = value; OnPropertyChanged(); }
    }

    public string IncludedFilesText
    {
        get => _includedFilesText;
        set { _includedFilesText = value; OnPropertyChanged(); }
    }

    public string ProgressActivity
    {
        get => _progressActivity;
        set { _progressActivity = value; OnPropertyChanged(); }
    }

    public string ProgressPath
    {
        get => _progressPath;
        set { _progressPath = value; OnPropertyChanged(); }
    }

    public string ProgressCounters
    {
        get => _progressCounters;
        set { _progressCounters = value; OnPropertyChanged(); }
    }

    public string ProgressPercentText
    {
        get => _progressPercentText;
        set { _progressPercentText = value; OnPropertyChanged(); }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set { _progressPercent = value; OnPropertyChanged(); }
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        set { _isProgressIndeterminate = value; OnPropertyChanged(); }
    }

    private void AddRoot()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Scegli una cartella o un disco da analizzare",
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(RootPath) && Directory.Exists(RootPath))
        {
            dialog.InitialDirectory = RootPath;
        }

        if (dialog.ShowDialog() == true)
        {
            RootPath = dialog.FolderName;
            OnPropertyChanged(nameof(RootPath));
        }

        AddRootIfValid(RootPath);
    }

    private void RemoveRoot()
    {
        if (SelectedRoot is not null) Roots.Remove(SelectedRoot);
        SaveSettings();
        RefreshCommands();
    }

    private void OpenSettings()
    {
        var dialog = new SettingsWindow(_settings)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true) return;
        var updated = dialog.Settings;
        _settings = dialog.Settings with
        {
            ScanRoots = updated.RememberScanRoots ? Roots.Select(r => r.Path).ToList() : []
        };
        SelectedMode = _settings.ScanMode;
        MinimumSizeText = _settings.MinimumSizeText;
        OnPropertyChanged(nameof(SelectedMode));
        OnPropertyChanged(nameof(MinimumSizeText));
        SaveSettings();
        Status = "Configurazione salvata.";
    }

    private async Task StartScanAsync(bool ignoreCache = false)
    {
        if (IsScanning)
        {
            Status = "Scansione gia in corso.";
            return;
        }

        if (Roots.Count == 0)
        {
            Status = "Aggiungi almeno una cartella.";
            return;
        }
        DuplicateGroups.Clear();
        _scanCts = new CancellationTokenSource();
        IsCancellationRequested = false;
        var mode = ignoreCache ? ScanMode.Full : Enum.Parse<ScanMode>(SelectedMode);
        var min = SizeParser.Parse(MinimumSizeText);
        var profile = ServiceCollectionExtensions.DefaultProfile(min, mode == ScanMode.Strict);
        var roots = Roots.Select(r => r.Path).ToArray();
        var lastProgressUtc = DateTime.MinValue;
        var progress = new Progress<ScanProgress>(p =>
        {
            var now = DateTime.UtcNow;
            if (p.Percent is null && p.Message is not ("Completed" or "Cancelled") && (now - lastProgressUtc).TotalMilliseconds < 250)
            {
                return;
            }

            lastProgressUtc = now;
            var translated = TranslateProgress(p.Message);
            Status = $"{translated}: {p.EnumeratedFiles:N0} file, {p.IncludedFiles:N0} inclusi, {p.HashedFiles:N0} hash";
            IncludedFilesText = p.IncludedFiles.ToString("N0");
            UpdateMainProgress(p, translated);
        });

        try
        {
            IsScanning = true;
            Status = ignoreCache ? "Scansione pulita avviata..." : "Scansione avviata...";
            UpdateMainProgress(new ScanProgress(0, 0, 0, null, "Enumerating"), "Enumerazione");
            var request = new ScanRequest(roots, mode, profile, _paths.DataRoot);
            var token = _scanCts.Token;
            var result = await Task.Run(() => _scanner.ScanAsync(request, progress, token), token);
            DuplicateGroups.Clear();
            foreach (var group in result.DuplicateGroups) DuplicateGroups.Add(group);
            Summary = $"Gruppi: {result.DuplicateGroups.Count}. Recuperabile: {result.ReclaimableBytes:N0} byte. Hard link esistenti: {result.ExistingHardLinks}.";
            GroupCountText = result.DuplicateGroups.Count.ToString("N0");
            ReclaimableText = FormatBytes(result.ReclaimableBytes);
            IncludedFilesText = result.IncludedFiles.ToString("N0");
            Status = result.WasCancelled ? "Scansione annullata. I dati raccolti sono stati salvati." : "Scansione completata.";
            IsProgressIndeterminate = false;
            ProgressPercent = 100;
            ProgressPercentText = "100%";
            await RefreshRootStatusesAsync();
        }
        catch (OperationCanceledException)
        {
            Status = "Scansione annullata.";
            IsProgressIndeterminate = false;
        }
        catch (Exception ex)
        {
            Status = "Scansione interrotta.";
            MessageBox.Show(ex.Message, "DuplicateVault", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsScanning = false;
            IsCancellationRequested = false;
            _scanCts.Dispose();
            _scanCts = null;
        }
    }

    private void CancelScan()
    {
        if (_scanCts is null || IsCancellationRequested) return;
        IsCancellationRequested = true;
        Status = "Annullamento richiesto. Preparero i risultati parziali appena possibile.";
        ProgressActivity = "Annullamento richiesto";
        ProgressPath = "Interrompo la lettura di nuovi file e preparo i risultati gia raccolti.";
        _scanCts.Cancel();
    }

    private async Task ReplaceAsync(bool dryRun)
    {
        if (SelectedGroup is null || SelectedFile is null)
        {
            Status = "Seleziona un duplicato.";
            return;
        }
        var master = SelectedGroup.Files.FirstOrDefault(f => f.IsMasterCandidate)?.Record.FullPath;
        if (master is null || master == SelectedFile.Record.FullPath)
        {
            Status = "Seleziona un file duplicato diverso dal master.";
            return;
        }
        var result = await _hardLinks.ReplaceWithHardLinkAsync(master, SelectedFile.Record.FullPath, new HardLinkOptions(DryRun: dryRun), CancellationToken.None);
        Status = $"{result.Status}: {result.Message}";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void AddRootIfValid(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Status = "Scegli una cartella da aggiungere.";
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            Status = "La cartella selezionata non esiste.";
            return;
        }

        if (Roots.All(r => !string.Equals(r.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            var item = new ScanRootItem(fullPath);
            Roots.Add(item);
            SelectedRoot = item;
            OnPropertyChanged(nameof(SelectedRoot));
            Status = "Cartella aggiunta.";
            _ = RefreshRootStatusAsync(item);
            SaveSettings();
            RefreshCommands();
        }
    }

    private void SaveSettings()
    {
        _settings = _settings with
        {
            ScanRoots = _settings.RememberScanRoots ? Roots.Select(r => r.Path).ToList() : []
        };
        _settingsStore.Save(_settings);
    }

    private void RefreshCommands()
    {
        _addRootCommand.RaiseCanExecuteChanged();
        _removeRootCommand.RaiseCanExecuteChanged();
        _settingsCommand.RaiseCanExecuteChanged();
        _startScanCommand.RaiseCanExecuteChanged();
        _startCleanScanCommand.RaiseCanExecuteChanged();
        _cancelScanCommand.RaiseCanExecuteChanged();
        _dryRunCommand.RaiseCanExecuteChanged();
        _executeHardLinkCommand.RaiseCanExecuteChanged();
    }

    private async Task RefreshRootStatusesAsync()
    {
        foreach (var root in Roots)
        {
            await RefreshRootStatusAsync(root);
        }
    }

    private async Task RefreshRootStatusAsync(ScanRootItem item)
    {
        try
        {
            item.Apply(await _database.GetScanRootStatusAsync(item.Path, CancellationToken.None));
        }
        catch
        {
            item.Apply(new ScanRootStatus(item.Path, ScanRootState.Unknown, null, 0));
        }
    }

    private void UpdateMainProgress(ScanProgress progress, string translatedMessage)
    {
        ProgressActivity = progress.Message switch
        {
            "ReusingPartialHash" => "Riutilizzo hash parziali salvati",
            "CalculatingPartialHash" => "Calcolo hash parziali",
            "ReusingFullHash" => "Riutilizzo hash completi salvati",
            "Hashing" => "Calcolo hash completi",
            "InspectingFileIdentity" => "Verifica hard link esistenti",
            "Persisting" => "Salvataggio risultati",
            _ => translatedMessage
        };
        ProgressPath = progress.CurrentPath ?? "Finalizzazione dei dati raccolti...";
        ProgressCounters = $"{progress.EnumeratedFiles:N0} letti, {progress.IncludedFiles:N0} inclusi, {progress.PartialHashes:N0} parziali, {progress.FullHashes:N0} completi, {progress.ReusedHashes:N0} riusati";
        if (progress.Percent is { } percent)
        {
            IsProgressIndeterminate = false;
            ProgressPercent = Math.Clamp(percent, 0, 100);
            ProgressPercentText = $"{ProgressPercent:N0}%";
        }
        else
        {
            IsProgressIndeterminate = true;
            ProgressPercentText = "";
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["byte", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} {units[unit]}" : $"{value:N1} {units[unit]}";
    }

    private static string TranslateProgress(string message) => message switch
    {
        "Enumerating" => "Enumerazione",
        "ReusingPartialHash" => "Riutilizzo hash parziali salvati",
        "CalculatingPartialHash" => "Calcolo hash parziali",
        "ReusingFullHash" => "Riutilizzo hash completi salvati",
        "Hashing" => "Calcolo hash",
        "InspectingFileIdentity" => "Verifica hard link esistenti",
        "Persisting" => "Salvataggio risultati",
        "Finalizing" => "Preparazione risultati parziali",
        "Completed" => "Completata",
        "Cancelled" => "Annullata",
        _ => message
    };
}

public sealed class ScanRootItem(string path) : INotifyPropertyChanged
{
    private ScanRootState _state = ScanRootState.Unknown;
    private DateTime? _lastScanUtc;
    private long _includedFiles;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Path { get; } = path;

    public string Symbol => _state switch
    {
        ScanRootState.Complete => "[OK]",
        ScanRootState.Partial => "[~]",
        _ => "[ ]"
    };

    public string StatusText => _state switch
    {
        ScanRootState.Complete => $"Scansione completa, {_includedFiles:N0} file inclusi",
        ScanRootState.Partial => $"Scansione parziale, {_includedFiles:N0} file inclusi",
        _ => "Mai scansionata"
    };

    public string LastScanText => _lastScanUtc is null ? "" : _lastScanUtc.Value.ToLocalTime().ToString("g");

    public void Apply(ScanRootStatus status)
    {
        _state = status.State;
        _lastScanUtc = status.LastScanUtc;
        _includedFiles = status.IncludedFiles;
        OnPropertyChanged(nameof(Symbol));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LastScanText));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
