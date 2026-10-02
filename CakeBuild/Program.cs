using System.IO.Compression;
using System.Text.Json;
using Cake.Common;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.Build;
using Cake.Core;
using Cake.Frosting;

namespace CakeBuild;

public static class Program
{
    public static int Main(string[] args) => new CakeHost().UseContext<BuildContext>().Run(args);
}

public sealed class BuildContext : FrostingContext
{
    public string Root { get; }
    public string BuildConfiguration { get; }
    public string ModId { get; }
    public string ModVersion { get; }

    public BuildContext(ICakeContext context) : base(context)
    {
        var directory = new DirectoryInfo(System.Environment.CurrentDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "AutoClay", "modinfo.json")))
            directory = directory.Parent;
        Root = directory?.FullName ?? throw new DirectoryNotFoundException("The Auto Clay project directory was not found.");
        BuildConfiguration = context.Argument("configuration", "Release");
        using var info = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "AutoClay", "modinfo.json")));
        ModId = info.RootElement.GetProperty("modid").GetString()!;
        ModVersion = info.RootElement.GetProperty("version").GetString()!;
    }
}

[TaskName("Build")]
public sealed class BuildTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(context.Root, "AutoClay", "assets"), "*.json", SearchOption.AllDirectories))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
        }
        context.DotNetBuild(Path.Combine(context.Root, "AutoClay", "AutoClay.csproj"), new DotNetBuildSettings
        {
            Configuration = context.BuildConfiguration,
            NoRestore = true
        });
    }
}

[TaskName("Package")]
[IsDependentOn(typeof(BuildTask))]
public sealed class PackageTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        string output = Path.Combine(context.Root, "AutoClay", "bin", context.BuildConfiguration, "Mods", "mod");
        string releases = Path.Combine(context.Root, "Releases");
        Directory.CreateDirectory(releases);
        string destination = Path.Combine(releases, $"{context.ModId}_{context.ModVersion}.zip");
        using var stream = File.Create(destination);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(output, file).Replace('\\', '/');
            if (relative == "AutoClay.dll" || relative == "modinfo.json" || relative.StartsWith("assets/", StringComparison.Ordinal))
                zip.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
        }
    }
}

[TaskName("Default")]
[IsDependentOn(typeof(PackageTask))]
public sealed class DefaultTask : FrostingTask;
