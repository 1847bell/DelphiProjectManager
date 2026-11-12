using System.Security;

namespace DelphiBuilder;

internal sealed record WorkspaceScanResult(
    HashSet<string> Projects,
    HashSet<string> PreservedPaths,
    List<string> Warnings);

internal static class WorkspaceScanner
{
    internal static WorkspaceScanResult Scan(string rootPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRoot(rootPath);

        var result = new WorkspaceScanResult(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            []);
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Recheck immediately before enumeration: an entry may have changed since its parent was read.
                var directoryAttributes = File.GetAttributes(directory);
                if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    Preserve(directory, "已跳过重解析点", result);
                    continue;
                }

                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(entry);
                    }
                    catch (Exception exception) when (IsAccessError(exception))
                    {
                        Preserve(entry, $"无法读取项目路径（{exception.Message}）", result);
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        Preserve(entry, "已跳过重解析点", result);
                    }
                    else if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                    }
                    else if (string.Equals(Path.GetExtension(entry), ".dproj", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Projects.Add(entry);
                    }
                }
            }
            catch (Exception exception) when (IsAccessError(exception))
            {
                // Enumeration can fail after yielding some entries. Preserve the entire subtree in that case.
                Preserve(directory, $"无法完整扫描目录（{exception.Message}）", result);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        ValidateRoot(rootPath);
        return result;
    }

    internal static void ValidateRoot(string rootPath)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(rootPath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new DirectoryNotFoundException($"工作区目录不存在，已保留已有项目：{rootPath}", exception);
        }

        if ((attributes & FileAttributes.Directory) == 0)
            throw new DirectoryNotFoundException($"工作区路径不是目录，已保留已有项目：{rootPath}");
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"不能扫描重解析点工作区，已保留已有项目：{rootPath}");
    }

    private static bool IsAccessError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException;

    private static void Preserve(string path, string reason, WorkspaceScanResult result)
    {
        if (result.PreservedPaths.Add(path))
            result.Warnings.Add($"{reason}：{path}；此路径下的已有项目已保留。");
    }
}
