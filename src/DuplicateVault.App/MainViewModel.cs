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
    private readonly GuiSettingsStore _settingsStore;
    private readonly RelayCommand _addRootCommand;
    private readonly RelayCommand _removeRootCommand;
    private readonly RelayCommand _settingsCommand;
    private readonly RelayCommand _startScanCommand;
    private readonly RelayCommand _cancelScanCommand;
    private readonly RelayCommand _dryRunCommand;
    private readonly RelayCommand _executeHardLinkCommand;
    private CancellationTokenSource? _scanCts;
    private GuiSettings _settings;
    private DuplicateGroup? _selectedGroup;
    private DuplicateFile? _selectedFile;
    private string? _selectedRoot;
    private string _status = "Pronto";
    private string _summary = "";
    private string _groupCountText = "0";
    private string _reclaimableText = "0 byte";
    private string _includedFilesText = "0";
    private bool _isScanning;

    public MainViewModel(IFileScanner scanner, IHardLinkService hardLinks, AppPaths paths, GuiSettingsStore settingsStore)
    {
        _scanner = scanner;
        _hardLinks = hardLinks;
        _paths = paths;
        _settingsStore = settingsStore;
        _settings = _settingsStore.Load();
        SelectedMode = _settings.ScanMode;
        MinimumSizeText = _settings.MinimumSizeText;
        if (_settings.RememberScanRoots)
        {
            foreach (var root in _settings.ScanRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Roots.Add(root);
            }
        }

        _addRootCommand = new RelayCommand(_ => AddRoot(), _ => !IsScanning);
        _removeRootCommand = new RelayCommand(_ => RemoveRoot(), _ => !IsScanning && SelectedRoot is not null);
        _settingsCommand = new RelayCommand(_ => OpenSettings(), _ => !IsScanning);
        _startScanCommand = new RelayCommand(async _ => await StartScanAsync(), _ => !IsScanning && Roots.Count > 0);
        _cancelScanCommand = new RelayCommand(_ => _scanCts?.Cancel(), _ => IsScanning);
        _dryRunCommand = new RelayCommand(async _ => await ReplaceAsync(true), _ => !IsScanning && SelectedGroup is not null && SelectedFile is not null);
        _executeHardLinkCommand = new RelayCommand(async _ => await ReplaceAsync(false), _ => !IsScanning && SelectedGroup is not null && SelectedFile is not null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<string> Roots { get; } = [];
    public ObservableCollection<DuplicateGroup> DuplicateGroups { get; } = [];
    public IReadOnlyList<string> Modes { get; } = ["Quick", "Full", "Strict"];
    public string RootPath { get; set; } = "";
    public string? SelectedRoot
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
    public ICommand CancelScanCommand => _cancelScanCommand;
    public ICommand DryRunCommand => _dryRunCommand;
    public ICommand ExecuteHardLinkCommand => _executeHardLinkCommand;

    public bool IsScanning
    {
        get => _isScanning;
        set { _isScanning = value; OnPropertyChanged(); RefreshCommands(); }
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
            ScanRoots = updated.RememberScanRoots ? Roots.ToList() : []
        };
        SelectedMode = _settings.ScanMode;
        MinimumSizeText = _settings.MinimumSizeText;
        OnPropertyChanged(nameof(SelectedMode));
        OnPropertyChanged(nameof(MinimumSizeText));
        SaveSettings();
        Status = "Configurazione salvata.";
    }

    private async Task StartScanAsync()
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
        var mode = Enum.Parse<ScanMode>(SelectedMode);
        var min = SizeParser.Parse(MinimumSizeText);
        var profile = ServiceCollectionExtensions.DefaultProfile(min, mode == ScanMode.Strict);
        var roots = Roots.ToArray();
        var lastProgressUtc = DateTime.MinValue;
        var progress = new Progress<ScanProgress>(p =>
        {
            var now = DateTime.UtcNow;
            if (p.Message != "Completed" && (now - lastProgressUtc).TotalMilliseconds < 250)
            {
                return;
            }

            lastProgressUtc = now;
            Status = $"{TranslateProgress(p.Message)}: {p.EnumeratedFiles:N0} file, {p.IncludedFiles:N0} inclusi, {p.HashedFiles:N0} hash";
            IncludedFilesText = p.IncludedFiles.ToString("N0");
        });

        try
        {
            IsScanning = true;
            Status = "Scansione avviata...";
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
        }
        catch (OperationCanceledException)
        {
            Status = "Scansione annullata.";
        }
        catch (Exception ex)
        {
            Status = "Scansione interrotta.";
            MessageBox.Show(ex.Message, "DuplicateVault", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsScanning = false;
            _scanCts.Dispose();
            _scanCts = null;
        }
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

        if (!Roots.Contains(fullPath))
        {
            Roots.Add(fullPath);
            SelectedRoot = fullPath;
            OnPropertyChanged(nameof(SelectedRoot));
            Status = "Cartella aggiunta.";
            SaveSettings();
            RefreshCommands();
        }
    }

    private void SaveSettings()
    {
        _settings = _settings with
        {
            ScanRoots = _settings.RememberScanRoots ? Roots.ToList() : []
        };
        _settingsStore.Save(_settings);
    }

    private void RefreshCommands()
    {
        _addRootCommand.RaiseCanExecuteChanged();
        _removeRootCommand.RaiseCanExecuteChanged();
        _settingsCommand.RaiseCanExecuteChanged();
        _startScanCommand.RaiseCanExecuteChanged();
        _cancelScanCommand.RaiseCanExecuteChanged();
        _dryRunCommand.RaiseCanExecuteChanged();
        _executeHardLinkCommand.RaiseCanExecuteChanged();
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
        "Hashing" => "Calcolo hash",
        "Completed" => "Completata",
        "Cancelled" => "Annullata",
        _ => message
    };
}

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
