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
    private readonly RelayCommand _setMasterCommand;
    private readonly Dictionary<string, string> _masterOverrides = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _scanCts;
    private GuiSettings _settings;
    private DuplicateGroup? _selectedGroup;
    private DuplicateFileItem? _selectedFile;
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
    private bool _isHardLinkOperationRunning;

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
            foreach (var root in _settings.ScanRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Roots.Add(new ScanRootItem(root));
            }
        }

        _addRootCommand = new RelayCommand(_ => AddRoot(), _ => !IsScanning && !IsHardLinkOperationRunning);
        _removeRootCommand = new RelayCommand(_ => RemoveRoot(), _ => !IsScanning && !IsHardLinkOperationRunning && SelectedRoot is not null);
        _settingsCommand = new RelayCommand(_ => OpenSettings(), _ => !IsScanning && !IsHardLinkOperationRunning);
        _startScanCommand = new RelayCommand(_ => BeginStartScan(false), _ => !IsScanning && !IsHardLinkOperationRunning && Roots.Count > 0);
        _startCleanScanCommand = new RelayCommand(_ => BeginStartScan(true), _ => !IsScanning && !IsHardLinkOperationRunning && Roots.Count > 0);
        _cancelScanCommand = new RelayCommand(_ => CancelScan(), _ => IsScanning && !IsCancellationRequested);
        _setMasterCommand = new RelayCommand(SetSelectedFileAsMaster, item => !IsScanning && !IsHardLinkOperationRunning && SelectedGroup is not null && item is DuplicateFileItem file && !file.IsMasterCandidate);
        _dryRunCommand = new RelayCommand(async _ => await ReplaceAllAsync(true), _ => CanRunHardLinkOperation);
        _executeHardLinkCommand = new RelayCommand(async _ => await ReplaceAllAsync(false), _ => CanRunHardLinkOperation);
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
    public ICommand SetMasterCommand => _setMasterCommand;
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

    public bool IsHardLinkOperationRunning
    {
        get => _isHardLinkOperationRunning;
        set { _isHardLinkOperationRunning = value; OnPropertyChanged(); RefreshCommands(); }
    }

    public bool CanRunHardLinkOperation => !IsScanning && !IsHardLinkOperationRunning && BuildAutomaticPlan().Items.Count > 0;

    public DuplicateGroup? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            _selectedGroup = value;
            _selectedFile = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedFile));
            OnPropertyChanged(nameof(SelectedFiles));
            RefreshCommands();
        }
    }

    public IReadOnlyList<DuplicateFileItem> SelectedFiles => SelectedGroup is null
        ? []
        : SelectedGroup.Files.Select(f => new DuplicateFileItem(f, GetMasterPath(SelectedGroup))).ToArray();

    public DuplicateFileItem? SelectedFile
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

    private async void BeginStartScan(bool ignoreCache)
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

        _scanCts = new CancellationTokenSource();
        IsCancellationRequested = false;
        IsScanning = true;
        var savedRoots = Roots.Select(r => r.Path).ToArray();
        Status = ignoreCache ? "Click ricevuto: avvio scansione pulita..." : "Click ricevuto: avvio scansione...";
        UpdateMainProgress(new ScanProgress(0, 0, 0, string.Join("; ", savedRoots), "Starting"), "Preparazione scansione");
        await Task.Yield();

        try
        {
            if (ignoreCache)
            {
                DuplicateGroups.Clear();
                await StartScanAsync(GetActiveRootPaths(), ignoreCache);
                return;
            }

            await LoadSavedDuplicateGroupsAsync(savedRoots);
            var scanRoots = await GetRootsNeedingScanAsync();
            if (scanRoots.Count == 0)
            {
                Status = "Risultati salvati caricati. Usa Scansione pulita per ricontrollare i file.";
                IsProgressIndeterminate = false;
                ProgressPercent = 100;
                ProgressPercentText = "100%";
                IsScanning = false;
                IsCancellationRequested = false;
                _scanCts?.Dispose();
                _scanCts = null;
                return;
            }

            await StartScanAsync(scanRoots, ignoreCache);
            await LoadSavedDuplicateGroupsAsync(savedRoots);
        }
        catch (Exception ex)
        {
            IsScanning = false;
            IsCancellationRequested = false;
            _scanCts?.Dispose();
            _scanCts = null;
            Status = "Errore durante l'avvio della scansione.";
            MessageBox.Show(ex.Message, "DuplicateVault", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task StartScanAsync(IReadOnlyList<string> roots, bool ignoreCache = false)
    {
        var scanCts = _scanCts ?? throw new InvalidOperationException("Scan cancellation token was not initialized.");
        var mode = ignoreCache ? ScanMode.Full : Enum.Parse<ScanMode>(SelectedMode);
        var min = SizeParser.Parse(MinimumSizeText);
        var profile = ServiceCollectionExtensions.DefaultProfile(min, mode == ScanMode.Strict);
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
            Status = ignoreCache ? "Scansione pulita avviata..." : "Scansione avviata...";
            UpdateMainProgress(new ScanProgress(0, 0, 0, string.Join("; ", roots), "Starting"), "Preparazione scansione");
            var request = new ScanRequest(roots, mode, profile, _paths.DataRoot);
            var token = scanCts.Token;
            var result = await Task.Run(() => _scanner.ScanAsync(request, progress, token), token);
            if (result.DuplicateGroups.Count > 0 || result.IncludedFiles > 0 || DuplicateGroups.Count == 0)
            {
                DuplicateGroups.Clear();
                foreach (var group in result.DuplicateGroups) DuplicateGroups.Add(group);
            }
            RefreshCommands();
            var visibleReclaimable = DuplicateGroups.Sum(g => g.ReclaimableBytes);
            Summary = $"Gruppi: {DuplicateGroups.Count}. Recuperabile: {visibleReclaimable:N0} byte. Hard link esistenti: {result.ExistingHardLinks}. Errori: {result.ErrorCount:N0}.";
            GroupCountText = DuplicateGroups.Count.ToString("N0");
            ReclaimableText = FormatBytes(visibleReclaimable);
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
            scanCts.Dispose();
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

    private async Task LoadSavedDuplicateGroupsAsync(IReadOnlyList<string> roots)
    {
        Status = "Caricamento gruppi duplicati salvati...";
        UpdateMainProgress(new ScanProgress(0, 0, 0, "Lettura database locale", "LoadingSavedGroups"), "Caricamento gruppi salvati");
        var savedGroups = await Task.Run(() => _database.GetSavedDuplicateGroupsAsync(roots, CancellationToken.None));
        savedGroups = await ReconcileAvailableHardLinksAsync(savedGroups);
        DuplicateGroups.Clear();
        foreach (var group in savedGroups) DuplicateGroups.Add(group);

        var reclaimable = savedGroups.Sum(g => g.ReclaimableBytes);
        var plan = BuildAutomaticPlan();
        GroupCountText = savedGroups.Count.ToString("N0");
        ReclaimableText = FormatBytes(reclaimable);
        Summary = savedGroups.Count == 0
            ? "Nessun gruppo duplicato salvato per le radici selezionate."
            : plan.Items.Count == 0 && plan.UnavailableFiles > 0
                ? $"Gruppi salvati: {savedGroups.Count:N0}. I file salvati non sono raggiungibili ora ({plan.UnavailableFiles:N0} file)."
                : $"Gruppi salvati: {savedGroups.Count:N0}. Recuperabile: {reclaimable:N0} byte.";
        RefreshCommands();
    }

    private async Task<IReadOnlyList<DuplicateGroup>> ReconcileAvailableHardLinksAsync(IReadOnlyList<DuplicateGroup> groups)
    {
        var refreshedRecords = new List<FileRecord>();
        var refreshedGroups = new List<DuplicateGroup>();
        foreach (var group in groups)
        {
            var files = new List<DuplicateFile>();
            foreach (var file in group.Files)
            {
                var record = file.Record;
                if (File.Exists(record.FullPath))
                {
                    try
                    {
                        var identity = await _hardLinks.GetFileIdentityAsync(record.FullPath, CancellationToken.None);
                        record = record with { FileId = identity.StableId, NumberOfLinks = identity.NumberOfLinks };
                        refreshedRecords.Add(record);
                    }
                    catch
                    {
                        // Keep saved metadata when the identity cannot be read.
                    }
                }

                files.Add(file with { Record = record });
            }

            refreshedGroups.Add(RebuildGroup(group, files));
        }

        if (refreshedRecords.Count > 0)
        {
            await _database.UpsertFilesAsync(refreshedRecords, CancellationToken.None);
        }

        return refreshedGroups;
    }

    private static DuplicateGroup RebuildGroup(DuplicateGroup group, IReadOnlyList<DuplicateFile> files)
    {
        var physicalGroups = files.GroupBy(f => f.Record.FileId ?? f.Record.FullPath).ToArray();
        var masterPath = ChooseMasterPath(files, physicalGroups);
        var rebuilt = new List<DuplicateFile>();
        foreach (var physicalGroup in physicalGroups)
        {
            var isExistingHardLink = physicalGroup.Count() > 1;
            foreach (var file in physicalGroup)
            {
                rebuilt.Add(file with
                {
                    IsExistingHardLink = isExistingHardLink,
                    IsMasterCandidate = string.Equals(file.Record.FullPath, masterPath, StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        var reclaimable = Math.Max(0, physicalGroups.Length - 1) * group.Length;
        return group with { Files = rebuilt, ReclaimableBytes = reclaimable };
    }

    private static string? ChooseMasterPath(IReadOnlyList<DuplicateFile> files, IEnumerable<IGrouping<string, DuplicateFile>> physicalGroups)
    {
        var linkedFiles = physicalGroups.Where(g => g.Count() > 1).SelectMany(g => g).ToArray();
        var candidates = linkedFiles.Length > 0 ? linkedFiles : files;
        return candidates
            .OrderBy(f => LooksLikeCopyName(f.Record.FileName))
            .ThenBy(f => f.Record.FullPath.Length)
            .ThenBy(f => f.Record.FullPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()?.Record.FullPath;
    }

    private static bool LooksLikeCopyName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return name.EndsWith(")", StringComparison.Ordinal) && name.LastIndexOf(" (", StringComparison.Ordinal) >= 0;
    }

    private async Task<IReadOnlyList<string>> GetRootsNeedingScanAsync()
    {
        var selected = GetActiveRootPaths();
        var statuses = await _database.GetScanRootStatusesAsync(selected, CancellationToken.None);
        return statuses
            .Where(s => s.State != ScanRootState.Complete)
            .Select(s => s.RootPath)
            .ToArray();
    }

    private IReadOnlyList<string> GetActiveRootPaths()
    {
        if (SelectedRoot is not null) return [SelectedRoot.Path];
        return Roots.Select(r => r.Path).ToArray();
    }

    private async Task ReplaceAllAsync(bool dryRun)
    {
        var plan = BuildAutomaticPlan();
        if (plan.Items.Count == 0)
        {
            Status = plan.UnavailableFiles > 0
                ? $"Nessun duplicato applicabile: {plan.UnavailableFiles:N0} file salvati non sono raggiungibili ora."
                : "Nessun duplicato applicabile.";
            return;
        }

        IsHardLinkOperationRunning = true;
        var succeeded = 0;
        var failed = 0;
        var processed = 0;
        var reasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var recordsByPath = DuplicateGroups
            .SelectMany(g => g.Files)
            .Select(f => f.Record)
            .GroupBy(r => r.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var verb = dryRun ? "Dry run" : "Creazione hard link";
        Status = $"{verb}: 0/{plan.Items.Count} file.";
        UpdateMainProgress(new ScanProgress(0, 0, 0, "Preparazione operazione hard link", "HardLinking", 0), verb);

        try
        {
            IProgress<HardLinkBatchProgress> progress = new Progress<HardLinkBatchProgress>(p =>
            {
                Status = $"{verb}: {p.Processed:N0}/{p.Total:N0} file.";
                ProgressActivity = verb;
                ProgressPath = p.CurrentPath;
                ProgressCounters = $"{p.Processed:N0} verificati, {p.Succeeded:N0} ok, {p.Failed:N0} non applicabili";
                IsProgressIndeterminate = false;
                ProgressPercent = p.Percent;
                ProgressPercentText = $"{p.Percent:N0}%";
            });

            await Task.Run(async () =>
            {
                foreach (var item in plan.Items)
                {
                    processed++;
                    var result = await _hardLinks.ReplaceWithHardLinkAsync(item.MasterPath, item.DuplicatePath, new HardLinkOptions(DryRun: dryRun), CancellationToken.None);
                    if (result.Status is HardLinkOperationStatus.Planned or HardLinkOperationStatus.Succeeded)
                    {
                        succeeded++;
                        if (!dryRun && result.Status == HardLinkOperationStatus.Succeeded)
                        {
                            await UpdateLinkedFileRecordsAsync(item.MasterPath, item.DuplicatePath, recordsByPath, CancellationToken.None);
                        }
                    }
                    else
                    {
                        failed++;
                        reasons[result.Message] = reasons.GetValueOrDefault(result.Message) + 1;
                    }

                    await _database.RecordHardLinkOperationAsync(0, item.MasterPath, item.DuplicatePath, result, CancellationToken.None);
                    progress.Report(new HardLinkBatchProgress(processed, plan.Items.Count, succeeded, failed, item.DuplicatePath));
                }
            });

            var mainReason = reasons.OrderByDescending(r => r.Value).FirstOrDefault();
            var reasonText = mainReason.Value > 0 ? $" Motivo principale: {mainReason.Key} ({mainReason.Value:N0})." : "";
            Status = dryRun
                ? $"Dry run completato: {succeeded:N0} pianificati, {failed:N0} non applicabili.{reasonText}"
                : $"Hard link completati: {succeeded:N0} creati, {failed:N0} non riusciti.{reasonText}";
            ProgressPercent = 100;
            ProgressPercentText = "100%";
            if (!dryRun && succeeded > 0)
            {
                await LoadSavedDuplicateGroupsAsync(GetActiveRootPaths());
            }
        }
        finally
        {
            IsHardLinkOperationRunning = false;
        }
    }

    private AutomaticPlan BuildAutomaticPlan()
    {
        var plan = new List<HardLinkPlanItem>();
        var unavailableFiles = 0;
        foreach (var group in DuplicateGroups)
        {
            var master = GetMasterPath(group);
            if (master is null) continue;
            var masterExists = File.Exists(master);
            if (!masterExists)
            {
                unavailableFiles += group.Files.Count(f => !f.IsExistingHardLink && !string.Equals(master, f.Record.FullPath, StringComparison.OrdinalIgnoreCase));
                continue;
            }

            foreach (var file in group.Files)
            {
                var duplicate = file.Record.FullPath;
                if (file.IsExistingHardLink || string.Equals(master, duplicate, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!File.Exists(duplicate))
                {
                    unavailableFiles++;
                    continue;
                }

                plan.Add(new HardLinkPlanItem(master, duplicate, true, "Automatic selection", file.Record.Length));
            }
        }

        return new AutomaticPlan(plan, unavailableFiles);
    }

    private async Task UpdateLinkedFileRecordsAsync(string masterPath, string duplicatePath, IReadOnlyDictionary<string, FileRecord> recordsByPath, CancellationToken cancellationToken)
    {
        if (!recordsByPath.TryGetValue(masterPath, out var masterRecord) || !recordsByPath.TryGetValue(duplicatePath, out var duplicateRecord))
        {
            return;
        }

        var identity = await _hardLinks.GetFileIdentityAsync(duplicatePath, cancellationToken);
        var masterIdentity = await _hardLinks.GetFileIdentityAsync(masterPath, cancellationToken);
        var linkCount = Math.Max(identity.NumberOfLinks, masterIdentity.NumberOfLinks);
        await _database.UpsertFilesAsync(
            [
                masterRecord with { FileId = identity.StableId, NumberOfLinks = linkCount },
                duplicateRecord with { FileId = identity.StableId, NumberOfLinks = linkCount }
            ],
            cancellationToken);
    }

    private void SetSelectedFileAsMaster(object? parameter)
    {
        if (SelectedGroup is null || parameter is not DuplicateFileItem file) return;
        _masterOverrides[GroupKey(SelectedGroup)] = file.Record.FullPath;
        SelectedFile = file;
        OnPropertyChanged(nameof(SelectedFiles));
        RefreshCommands();
        Status = "Master aggiornato per il gruppo selezionato.";
    }

    private string? GetMasterPath(DuplicateGroup group)
    {
        if (_masterOverrides.TryGetValue(GroupKey(group), out var overridePath) && group.Files.Any(f => string.Equals(f.Record.FullPath, overridePath, StringComparison.OrdinalIgnoreCase)))
        {
            return overridePath;
        }

        return group.Files.FirstOrDefault(f => f.IsMasterCandidate)?.Record.FullPath;
    }

    private static string GroupKey(DuplicateGroup group) => $"{group.Length}|{group.FullHash}";

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
        _setMasterCommand.RaiseCanExecuteChanged();
        _dryRunCommand.RaiseCanExecuteChanged();
        _executeHardLinkCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanRunHardLinkOperation));
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
            "OpeningRoot" => "Apertura radice scansione",
            "OpeningDirectory" => "Lettura cartelle",
            "RootUnavailable" => "Radice non raggiungibile",
            "LoadingSavedGroups" => "Caricamento gruppi salvati",
            "HardLinking" => "Operazione hard link",
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
        "Starting" => "Preparazione scansione",
        "OpeningRoot" => "Apertura radice scansione",
        "OpeningDirectory" => "Lettura cartelle",
        "RootUnavailable" => "Radice non raggiungibile",
        "LoadingSavedGroups" => "Caricamento gruppi salvati",
        "HardLinking" => "Operazione hard link",
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

public sealed class DuplicateFileItem(DuplicateFile file, string? masterPath)
{
    public FileRecord Record => file.Record;
    public bool IsAvailable => File.Exists(file.Record.FullPath);
    public bool IsMasterCandidate => string.Equals(file.Record.FullPath, masterPath, StringComparison.OrdinalIgnoreCase);
    public bool IsExistingHardLink => file.IsExistingHardLink;
    public bool IsPlannedLink => !IsMasterCandidate && !file.IsExistingHardLink;
    public string LengthText => FormatBytes(file.Record.Length);

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
}

public sealed record AutomaticPlan(IReadOnlyList<HardLinkPlanItem> Items, int UnavailableFiles);

public sealed record HardLinkBatchProgress(int Processed, int Total, int Succeeded, int Failed, string CurrentPath)
{
    public double Percent => Total <= 0 ? 100 : Math.Clamp(Processed * 100.0 / Total, 0, 100);
}

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
