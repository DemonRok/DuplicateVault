using System.Security.Cryptography;
using System.Xml.Linq;

var command = args.FirstOrDefault() ?? "help";
try
{
    return command switch
    {
        "read-version" => ReadVersion(),
        "validate-version" => ValidateVersion(),
        "increment-version" => IncrementVersion(),
        "checksum" => Checksum(args.Skip(1).FirstOrDefault()),
        "validate-package" => ValidatePackage(args.Skip(1).FirstOrDefault()),
        _ => Help()
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static string PropsPath()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, "Directory.Build.props");
        if (File.Exists(candidate)) return candidate;
        directory = directory.Parent;
    }
    throw new FileNotFoundException("Directory.Build.props was not found.");
}

static (XDocument Document, XElement Prefix, XElement Build) Load()
{
    var document = XDocument.Load(PropsPath(), LoadOptions.PreserveWhitespace);
    var prefix = document.Descendants("VersionPrefix").Single();
    var build = document.Descendants("VersionBuild").Single();
    return (document, prefix, build);
}

static int ReadVersion()
{
    var (_, prefix, build) = Load();
    Console.WriteLine($"{prefix.Value}.{build.Value}");
    return 0;
}

static int ValidateVersion()
{
    var (_, prefix, build) = Load();
    if (!Version.TryParse($"{prefix.Value}.{build.Value}", out var version) || version.Build < 0) throw new InvalidOperationException("Directory.Build.props contains an invalid MAJOR.MINOR.PATCH.BUILD version.");
    Console.WriteLine(version);
    return 0;
}

static int IncrementVersion()
{
    var (document, prefix, build) = Load();
    if (!int.TryParse(build.Value, out var current) || current < 0) throw new InvalidOperationException("VersionBuild is invalid.");
    var oldVersion = $"{prefix.Value}.{current}";
    build.Value = (current + 1).ToString();
    document.Save(PropsPath(), SaveOptions.DisableFormatting);
    Console.WriteLine($"Old version: {oldVersion}");
    Console.WriteLine($"New version: {prefix.Value}.{build.Value}");
    return 0;
}

static int Checksum(string? path)
{
    if (path is null) throw new ArgumentException("checksum requires a file path.");
    using var stream = File.OpenRead(path);
    var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    var output = $"{hash}  {Path.GetFileName(path)}";
    File.WriteAllText(path + ".sha256", output + Environment.NewLine);
    Console.WriteLine(output);
    return 0;
}

static int ValidatePackage(string? path)
{
    if (path is null || !File.Exists(path)) throw new ArgumentException("validate-package requires an existing package path.");
    if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Release package must be a ZIP file.");
    if (new FileInfo(path).Length == 0) throw new InvalidOperationException("Release package is empty.");
    Console.WriteLine("Package validation succeeded.");
    return 0;
}

static int Help()
{
    Console.WriteLine("Commands: read-version, validate-version, increment-version, checksum <file>, validate-package <zip>");
    return 0;
}
