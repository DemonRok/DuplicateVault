using System.Reflection;
using System.Runtime.InteropServices;
using DuplicateVault.Core;
using DuplicateVault.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var exitCode = await RunAsync(args);
return exitCode;

static async Task<int> RunAsync(string[] args)
{
    var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
    var dataRoot = Option(args, "--data-root") ?? AppContext.BaseDirectory;
    using var host = Host.CreateDefaultBuilder(args)
        .ConfigureLogging(b => b.ClearProviders().AddConsole())
        .ConfigureServices(s => s.AddDuplicateVault(dataRoot))
        .Build();

    var database = host.Services.GetRequiredService<IDuplicateVaultDatabase>();
    await database.InitializeAsync(CancellationToken.None);

    try
    {
        return command switch
        {
            "version" => Version(host.Services.GetRequiredService<AppPaths>()),
            "scan" => await ScanAsync(args, host.Services),
            "plan" => await PlanAsync(host.Services),
            "dedupe" => await DedupeAsync(args, host.Services),
            "db" => await DbAsync(args, host.Services),
            _ => Help()
        };
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Operation cancelled.");
        return 130;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static int Version(AppPaths paths)
{
    var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0.0";
    Console.WriteLine($"DuplicateVault {version}");
    Console.WriteLine("Runtime: .NET 10");
    Console.WriteLine($"Operating system: {Environment.OSVersion}");
    Console.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
    Console.WriteLine($"Data root: {paths.DataRoot}");
    Console.WriteLine($"Database: {paths.DatabasePath}");
    return 0;
}

static async Task<int> ScanAsync(string[] args, IServiceProvider services)
{
    var roots = Values(args, "--root").ToArray();
    if (roots.Length == 0) throw new ArgumentException("At least one --root path is required.");
    var mode = (Option(args, "--mode") ?? "quick").ToLowerInvariant() switch
    {
        "quick" => ScanMode.Quick,
        "full" => ScanMode.Full,
        "strict" => ScanMode.Strict,
        _ => throw new ArgumentException("Invalid --mode. Use quick, full, or strict.")
    };
    var minimum = Option(args, "--min-size") is { } value ? SizeParser.Parse(value) : 1024 * 1024;
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    var profile = ServiceCollectionExtensions.DefaultProfile(minimum, mode == ScanMode.Strict);
    var scanner = services.GetRequiredService<IFileScanner>();
    var paths = services.GetRequiredService<AppPaths>();
    var progress = new Progress<ScanProgress>(p => Console.WriteLine($"{p.Message}: {p.EnumeratedFiles} enumerated, {p.IncludedFiles} included, {p.HashedFiles} hashed"));
    var result = await scanner.ScanAsync(new ScanRequest(roots, mode, profile, paths.DataRoot), progress, cts.Token);
    if (args.Contains("--json"))
    {
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
    else
    {
        Console.WriteLine($"Duplicate groups: {result.DuplicateGroups.Count}");
        Console.WriteLine($"Reclaimable bytes: {result.ReclaimableBytes}");
    }
    return result.WasCancelled ? 130 : 0;
}

static async Task<int> PlanAsync(IServiceProvider services)
{
    var plan = await services.GetRequiredService<IDuplicateVaultDatabase>().GetLatestPlanAsync(CancellationToken.None);
    foreach (var item in plan) Console.WriteLine($"{(item.IsEligible ? "ELIGIBLE" : "SKIP")} {item.DuplicatePath} -> {item.MasterPath} ({item.Reason})");
    Console.WriteLine($"Items: {plan.Count}");
    return 0;
}

static async Task<int> DedupeAsync(string[] args, IServiceProvider services)
{
    var master = Option(args, "--master") ?? throw new ArgumentException("--master is required.");
    var duplicate = Option(args, "--duplicate") ?? throw new ArgumentException("--duplicate is required.");
    var yes = args.Contains("--yes");
    var dryRun = args.Contains("--dry-run");
    if (!yes && !dryRun) throw new ArgumentException("Use --yes to confirm or --dry-run to preview.");
    var service = services.GetRequiredService<IHardLinkService>();
    var result = await service.ReplaceWithHardLinkAsync(master, duplicate, new HardLinkOptions(DryRun: dryRun), CancellationToken.None);
    await services.GetRequiredService<IDuplicateVaultDatabase>().RecordHardLinkOperationAsync(0, master, duplicate, result, CancellationToken.None);
    Console.WriteLine($"{result.Status}: {result.Message}");
    return result.Status is HardLinkOperationStatus.Succeeded or HardLinkOperationStatus.Planned ? 0 : 2;
}

static async Task<int> DbAsync(string[] args, IServiceProvider services)
{
    if (!args.Contains("stats")) return Help();
    var paths = services.GetRequiredService<AppPaths>();
    Console.WriteLine($"Database: {paths.DatabasePath}");
    Console.WriteLine($"Exists: {File.Exists(paths.DatabasePath)}");
    Console.WriteLine($"Size: {(File.Exists(paths.DatabasePath) ? new FileInfo(paths.DatabasePath).Length : 0)} bytes");
    await Task.CompletedTask;
    return 0;
}

static string? Option(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static IEnumerable<string> Values(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name) yield return args[i + 1];
    }
}

static int Help()
{
    Console.WriteLine("DuplicateVault commands:");
    Console.WriteLine("  version");
    Console.WriteLine("  scan --root <path> [--mode quick|full|strict] [--min-size 1MiB] [--json]");
    Console.WriteLine("  plan");
    Console.WriteLine("  dedupe --master <path> --duplicate <path> [--dry-run|--yes]");
    Console.WriteLine("  db stats");
    return 0;
}
