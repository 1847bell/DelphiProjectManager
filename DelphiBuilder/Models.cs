namespace DelphiBuilder;

public sealed record WorkspaceRecord(long Id, string Name, string RootPath);

public sealed record ProjectRecord(long Id, long WorkspaceId, string FilePath,
    string ActiveConfiguration = "", string Platform = "Win32")
{
    public string Name => Path.GetFileNameWithoutExtension(FilePath);
}

public sealed record FavoriteFolderRecord(long Id, string Name, int SortOrder);
public sealed record FavoriteRecord(long Id, long FolderId, ProjectRecord Project, int SortOrder);
public sealed record ScanSummary(int ProjectCount, IReadOnlyList<string> Warnings);

public sealed class AppSettings
{
    public string DelphiVersion { get; set; } = "XE7";
    public string CompilerPath32 { get; set; } = "";
    public string CompilerPath64 { get; set; } = "";
    public string RsVarsPath { get; set; } = "";
    public string ResourceCompilerPath { get; set; } = "";
    public bool ResourceBuild { get; set; } = true;
    public int OutputPathHistoryLimit { get; set; } = 5;
    public string OutputEncoding { get; set; } = "system";
    public List<string> AdditionalArguments { get; set; } = [];
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ProjectConfiguration(string Name, string Key, string? ParentKey = null);
public sealed record ProjectResourceItem(string Kind, string Include, string? Suffix = null);

public sealed class ProjectEvaluation
{
    public required string ProjectFile { get; init; }
    public required string MainSource { get; init; }
    public required string Configuration { get; init; }
    public required string Platform { get; init; }
    public IReadOnlyList<ProjectConfiguration> Configurations { get; init; } = [];
    public Dictionary<string, string> Properties { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<ProjectResourceItem> ResourceItems { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string Version { get; init; } = "1.0.0.0";
    public string OutputPath { get; init; } = "";
}

public sealed record ResourceBuildStep(string Executable, IReadOnlyList<string> Arguments,
    string Input, string Output);

public sealed class BuildPlan
{
    public required ProjectEvaluation Evaluation { get; init; }
    public required string CompilerPath { get; init; }
    public required string WorkingDirectory { get; init; }
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public IReadOnlyList<ResourceBuildStep> ResourceSteps { get; init; } = [];
    public string? ProjectResourcePath { get; init; }
    public IReadOnlyList<string> ExpectedArtifacts { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string OutputEncoding { get; init; } = "system";
}

public sealed record BuildResult(int ExitCode, bool Cancelled, TimeSpan Duration);
