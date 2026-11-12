using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DelphiBuilder;

public sealed class AppStore : IDisposable
{
    private const string CaseInsensitiveCollation = "ORDINAL_IGNORE_CASE";
    private readonly string _connectionString;
    private int _disposed;

    public AppStore(string databasePath)
    {
        var fullPath = Path.GetFullPath(RequireText(databasePath, nameof(databasePath), "数据库路径不能为空。"));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            DefaultTimeout = 30,
            Pooling = false
        }.ToString();

        using var connection = OpenConnection();
        using (var pragma = Command(connection, null, "PRAGMA journal_mode = WAL;"))
            pragma.ExecuteScalar();
        using var transaction = connection.BeginTransaction();
        using var command = Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS Workspaces (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL CHECK(length(trim(Name)) > 0),
                RootPath TEXT NOT NULL COLLATE ORDINAL_IGNORE_CASE UNIQUE CHECK(length(RootPath) > 0)
            );
            CREATE TABLE IF NOT EXISTS Projects (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                WorkspaceId INTEGER NOT NULL REFERENCES Workspaces(Id) ON DELETE CASCADE,
                FilePath TEXT NOT NULL COLLATE ORDINAL_IGNORE_CASE CHECK(length(FilePath) > 0),
                ActiveConfiguration TEXT NOT NULL DEFAULT '',
                Platform TEXT NOT NULL DEFAULT 'Win32' CHECK(length(trim(Platform)) > 0),
                UNIQUE(WorkspaceId, FilePath)
            );
            CREATE TABLE IF NOT EXISTS OutputHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                OutputPath TEXT NOT NULL COLLATE ORDINAL_IGNORE_CASE CHECK(length(trim(OutputPath)) > 0),
                UNIQUE(ProjectId, OutputPath)
            );
            CREATE TABLE IF NOT EXISTS Settings (
                Id INTEGER PRIMARY KEY CHECK(Id = 1),
                Value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS UiState (
                Key TEXT PRIMARY KEY NOT NULL CHECK(length(Key) > 0),
                Value TEXT NOT NULL
            );
            """);
        command.ExecuteNonQuery();
        InitializeFavorites(connection, transaction);
        transaction.Commit();
    }

    private static void InitializeFavorites(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var check = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'FavoriteFolders')
                AND NOT EXISTS(SELECT 1 FROM pragma_table_info('FavoriteFolders') WHERE name = 'WorkspaceId');
            """);
        var legacy = Convert.ToInt32(check.ExecuteScalar()) != 0;
        if (legacy)
        {
            using var rename = Command(connection, transaction, """
                ALTER TABLE Favorites RENAME TO LegacyFavorites;
                ALTER TABLE FavoriteFolders RENAME TO LegacyFavoriteFolders;
                """);
            rename.ExecuteNonQuery();
        }
        using (var create = Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS FavoriteFolders (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                WorkspaceId INTEGER REFERENCES Workspaces(Id) ON DELETE CASCADE,
                Name TEXT NOT NULL COLLATE ORDINAL_IGNORE_CASE CHECK(length(trim(Name)) > 0),
                SortOrder INTEGER NOT NULL CHECK(SortOrder >= 0),
                UNIQUE(WorkspaceId, Name)
            );
            CREATE TABLE IF NOT EXISTS Favorites (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FolderId INTEGER NOT NULL REFERENCES FavoriteFolders(Id) ON DELETE CASCADE,
                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                SortOrder INTEGER NOT NULL CHECK(SortOrder >= 0),
                UNIQUE(FolderId, ProjectId)
            );
            """))
            create.ExecuteNonQuery();
        if (legacy)
        {
            // Keep the old folder ID for its first owner, and split mixed folders by project workspace.
            // Only legacy empty folders with no remaining workspace may temporarily have a null owner.
            using var migrate = Command(connection, transaction, """
                INSERT INTO FavoriteFolders(Id, WorkspaceId, Name, SortOrder)
                SELECT folder.Id, COALESCE(
                    (SELECT MIN(project.WorkspaceId) FROM LegacyFavorites AS favorite
                     JOIN Projects AS project ON project.Id = favorite.ProjectId WHERE favorite.FolderId = folder.Id),
                    (SELECT Id FROM Workspaces WHERE Id = (SELECT Value FROM UiState WHERE Key = 'workspace')),
                    (SELECT MIN(Id) FROM Workspaces)), folder.Name, folder.SortOrder
                FROM LegacyFavoriteFolders AS folder;

                INSERT INTO FavoriteFolders(WorkspaceId, Name, SortOrder)
                SELECT DISTINCT project.WorkspaceId, folder.Name, folder.SortOrder
                FROM LegacyFavoriteFolders AS folder
                JOIN LegacyFavorites AS favorite ON favorite.FolderId = folder.Id
                JOIN Projects AS project ON project.Id = favorite.ProjectId
                WHERE NOT EXISTS(SELECT 1 FROM FavoriteFolders AS scoped
                                 WHERE scoped.WorkspaceId = project.WorkspaceId AND scoped.Name = folder.Name);

                INSERT INTO Favorites(Id, FolderId, ProjectId, SortOrder)
                SELECT favorite.Id, scoped.Id, favorite.ProjectId, favorite.SortOrder
                FROM LegacyFavorites AS favorite
                JOIN LegacyFavoriteFolders AS folder ON folder.Id = favorite.FolderId
                JOIN Projects AS project ON project.Id = favorite.ProjectId
                JOIN FavoriteFolders AS scoped ON scoped.WorkspaceId = project.WorkspaceId AND scoped.Name = folder.Name;
                """);
            migrate.ExecuteNonQuery();
            using var verify = Command(connection, transaction,
                "SELECT (SELECT COUNT(*) FROM LegacyFavorites) = (SELECT COUNT(*) FROM Favorites);");
            if (Convert.ToInt32(verify.ExecuteScalar()) == 0)
                throw new InvalidDataException("偏好迁移未能保留全部项目引用，原数据库未被修改。");
            using var drop = Command(connection, transaction, """
                DROP TABLE LegacyFavorites;
                DROP TABLE LegacyFavoriteFolders;
                """);
            drop.ExecuteNonQuery();
        }
        using var constraints = Command(connection, transaction, """
            CREATE INDEX IF NOT EXISTS IX_FavoriteFolders_Order ON FavoriteFolders(WorkspaceId, SortOrder, Id);
            CREATE INDEX IF NOT EXISTS IX_Favorites_ProjectId ON Favorites(ProjectId);
            CREATE INDEX IF NOT EXISTS IX_Favorites_Order ON Favorites(FolderId, SortOrder, Id);
            CREATE TRIGGER IF NOT EXISTS Favorites_Workspace_Insert BEFORE INSERT ON Favorites
            WHEN NOT EXISTS(SELECT 1 FROM FavoriteFolders AS folder JOIN Projects AS project
                            ON project.WorkspaceId = folder.WorkspaceId
                            WHERE folder.Id = NEW.FolderId AND project.Id = NEW.ProjectId)
            BEGIN
                SELECT RAISE(ABORT, '偏好文件夹和项目必须属于同一工作区。');
            END;
            CREATE TRIGGER IF NOT EXISTS Favorites_Workspace_Update BEFORE UPDATE OF FolderId, ProjectId ON Favorites
            WHEN NOT EXISTS(SELECT 1 FROM FavoriteFolders AS folder JOIN Projects AS project
                            ON project.WorkspaceId = folder.WorkspaceId
                            WHERE folder.Id = NEW.FolderId AND project.Id = NEW.ProjectId)
            BEGIN
                SELECT RAISE(ABORT, '偏好文件夹和项目必须属于同一工作区。');
            END;
            """);
        constraints.ExecuteNonQuery();
    }

    public IReadOnlyList<WorkspaceRecord> GetWorkspaces()
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, "SELECT Id, Name, RootPath FROM Workspaces ORDER BY Id;");
        using var reader = command.ExecuteReader();
        var records = new List<WorkspaceRecord>();
        while (reader.Read())
            records.Add(ReadWorkspace(reader));
        return records;
    }

    public WorkspaceRecord AddWorkspace(string rootPath)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(RequireText(rootPath, nameof(rootPath), "工作区路径不能为空。")));
        var name = Path.GetFileName(normalizedPath);
        if (string.IsNullOrEmpty(name))
            name = normalizedPath;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var insert = Command(connection, transaction, """
            INSERT INTO Workspaces(Name, RootPath) VALUES($name, $root)
            ON CONFLICT(RootPath) DO NOTHING;
            """, ("$name", name), ("$root", normalizedPath)))
            insert.ExecuteNonQuery();
        using var select = Command(connection, transaction,
            "SELECT Id, Name, RootPath FROM Workspaces WHERE RootPath = $root;", ("$root", normalizedPath));
        WorkspaceRecord record;
        using (var reader = select.ExecuteReader())
        {
            if (!reader.Read())
                throw new InvalidOperationException("无法读取新增的工作区。");
            record = ReadWorkspace(reader);
        }
        using (var claim = Command(connection, transaction,
            "UPDATE FavoriteFolders SET WorkspaceId = $workspace WHERE WorkspaceId IS NULL;", ("$workspace", record.Id)))
            claim.ExecuteNonQuery();
        transaction.Commit();
        return record;
    }

    public void RemoveWorkspace(long id)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, "DELETE FROM Workspaces WHERE Id = $id;", ("$id", id));
        command.ExecuteNonQuery();
    }

    public ScanSummary ScanWorkspace(WorkspaceRecord workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        cancellationToken.ThrowIfCancellationRequested();
        string rootPath;
        using (var connection = OpenConnection())
        using (var command = Command(connection, null, "SELECT RootPath FROM Workspaces WHERE Id = $id;", ("$id", workspace.Id)))
            rootPath = command.ExecuteScalar() as string ?? throw new InvalidOperationException("工作区已被移除。");

        // The caller runs this synchronous filesystem work on its worker thread. No connection is held during traversal.
        var scan = WorkspaceScanner.Scan(rootPath, cancellationToken);
        using var writeConnection = OpenConnection();
        using var transaction = writeConnection.BeginTransaction();
        using (var check = Command(writeConnection, transaction,
            "SELECT RootPath FROM Workspaces WHERE Id = $id;", ("$id", workspace.Id)))
        {
            if (check.ExecuteScalar() is not string currentRoot || !StringComparer.OrdinalIgnoreCase.Equals(currentRoot, rootPath))
                throw new InvalidOperationException("扫描期间工作区已被移除或更改。");
        }

        var existing = new List<(long Id, string Path)>();
        using (var select = Command(writeConnection, transaction,
            "SELECT Id, FilePath FROM Projects WHERE WorkspaceId = $workspace;", ("$workspace", workspace.Id)))
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
                existing.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        using (var insert = Command(writeConnection, transaction, """
            INSERT INTO Projects(WorkspaceId, FilePath) VALUES($workspace, $path)
            ON CONFLICT(WorkspaceId, FilePath) DO UPDATE SET FilePath = excluded.FilePath;
            """, ("$workspace", workspace.Id), ("$path", "")))
        {
            foreach (var filePath in scan.Projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                insert.Parameters["$path"].Value = filePath;
                insert.ExecuteNonQuery();
            }
        }
        using (var delete = Command(writeConnection, transaction, "DELETE FROM Projects WHERE Id = $id;", ("$id", 0L)))
        {
            foreach (var project in existing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (scan.Projects.Contains(project.Path) || IsPreserved(project.Path, scan.PreservedPaths))
                    continue;
                delete.Parameters["$id"].Value = project.Id;
                delete.ExecuteNonQuery();
            }
        }

        int projectCount;
        using (var count = Command(writeConnection, transaction,
            "SELECT COUNT(*) FROM Projects WHERE WorkspaceId = $workspace;", ("$workspace", workspace.Id)))
            projectCount = Convert.ToInt32(count.ExecuteScalar());
        WorkspaceScanner.ValidateRoot(rootPath);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return new ScanSummary(projectCount, scan.Warnings);
    }

    public IReadOnlyList<ProjectRecord> GetProjects(long workspaceId)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, """
            SELECT Id, WorkspaceId, FilePath, ActiveConfiguration, Platform
            FROM Projects WHERE WorkspaceId = $workspace ORDER BY FilePath, Id;
            """, ("$workspace", workspaceId));
        using var reader = command.ExecuteReader();
        var records = new List<ProjectRecord>();
        while (reader.Read())
            records.Add(ReadProject(reader));
        return records;
    }

    public ProjectRecord? GetProject(long id)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, """
            SELECT Id, WorkspaceId, FilePath, ActiveConfiguration, Platform FROM Projects WHERE Id = $id;
            """, ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProject(reader) : null;
    }

    public void SetActiveTarget(long projectId, string configuration, string platform)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        platform = RequireText(platform, nameof(platform), "目标平台不能为空。");
        using var connection = OpenConnection();
        using var command = Command(connection, null, """
            UPDATE Projects SET ActiveConfiguration = $configuration, Platform = $platform WHERE Id = $id;
            """, ("$configuration", configuration.Trim()), ("$platform", platform), ("$id", projectId));
        if (command.ExecuteNonQuery() == 0)
            throw new InvalidOperationException("项目已被移除，无法设置当前编译目标。");
    }

    public IReadOnlyList<FavoriteFolderRecord> GetFavoriteFolders(long workspaceId)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null,
            "SELECT Id, Name, SortOrder FROM FavoriteFolders WHERE WorkspaceId = $workspace ORDER BY SortOrder, Id;",
            ("$workspace", workspaceId));
        using var reader = command.ExecuteReader();
        var records = new List<FavoriteFolderRecord>();
        while (reader.Read())
            records.Add(new FavoriteFolderRecord(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
        return records;
    }

    public FavoriteFolderRecord AddFavoriteFolder(long workspaceId, string name)
    {
        name = RequireText(name, nameof(name), "偏好文件夹名称不能为空。");
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var insert = Command(connection, transaction, """
            INSERT INTO FavoriteFolders(WorkspaceId, Name, SortOrder)
            VALUES($workspace, $name, (SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM FavoriteFolders WHERE WorkspaceId = $workspace))
            ON CONFLICT(WorkspaceId, Name) DO NOTHING;
            """, ("$workspace", workspaceId), ("$name", name)))
            insert.ExecuteNonQuery();
        using var select = Command(connection, transaction,
            "SELECT Id, Name, SortOrder FROM FavoriteFolders WHERE WorkspaceId = $workspace AND Name = $name;",
            ("$workspace", workspaceId), ("$name", name));
        FavoriteFolderRecord record;
        using (var reader = select.ExecuteReader())
        {
            if (!reader.Read())
                throw new InvalidOperationException("无法读取新增的偏好文件夹。");
            record = new FavoriteFolderRecord(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2));
        }
        transaction.Commit();
        return record;
    }

    public void RenameFavoriteFolder(long folderId, string name)
    {
        name = RequireText(name, nameof(name), "偏好文件夹名称不能为空。");
        using var connection = OpenConnection();
        using var command = Command(connection, null,
            "UPDATE FavoriteFolders SET Name = $name WHERE Id = $id;", ("$name", name), ("$id", folderId));
        try
        {
            if (command.ExecuteNonQuery() == 0)
                throw new InvalidOperationException("偏好文件夹已被移除。");
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("同名偏好文件夹已存在。", exception);
        }
    }

    public void RemoveFavoriteFolder(long folderId)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, "DELETE FROM FavoriteFolders WHERE Id = $id;", ("$id", folderId));
        command.ExecuteNonQuery();
    }

    public void MoveFavoriteFolder(long folderId, int delta) => MoveOrderedItem(folderId, delta, false);

    public IReadOnlyList<FavoriteRecord> GetFavorites(long folderId)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, """
            SELECT f.Id, f.FolderId, f.SortOrder,
                   p.Id, p.WorkspaceId, p.FilePath, p.ActiveConfiguration, p.Platform
            FROM Favorites AS f JOIN Projects AS p ON p.Id = f.ProjectId
            WHERE f.FolderId = $folder ORDER BY f.SortOrder, f.Id;
            """, ("$folder", folderId));
        using var reader = command.ExecuteReader();
        var records = new List<FavoriteRecord>();
        while (reader.Read())
            records.Add(new FavoriteRecord(reader.GetInt64(0), reader.GetInt64(1), ReadProject(reader, 3), reader.GetInt32(2)));
        return records;
    }

    public void AddFavorite(long folderId, long projectId)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, """
            INSERT INTO Favorites(FolderId, ProjectId, SortOrder)
            VALUES($folder, $project, (SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM Favorites WHERE FolderId = $folder))
            ON CONFLICT(FolderId, ProjectId) DO NOTHING;
            """, ("$folder", folderId), ("$project", projectId));
        command.ExecuteNonQuery();
    }

    public void RemoveFavorite(long favoriteId)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, "DELETE FROM Favorites WHERE Id = $id;", ("$id", favoriteId));
        command.ExecuteNonQuery();
    }

    public void MoveFavorite(long favoriteId, int delta) => MoveOrderedItem(favoriteId, delta, true);

    public AppSettings LoadSettings()
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, "SELECT Value FROM Settings WHERE Id = 1;");
        if (command.ExecuteScalar() is not string json)
            return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json)
                ?? throw new InvalidDataException("设置数据为空，无法读取。");
            if (settings.AdditionalArguments is null || settings.Environment is null)
                throw new InvalidDataException("设置中的附加参数或环境变量数据无效。");
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in settings.Environment)
                environment[entry.Key] = entry.Value;
            settings.Environment = environment;
            return settings;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("设置数据格式无效，已有设置未被覆盖。", exception);
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(settings);
        using var connection = OpenConnection();
        using var command = Command(connection, null, """
            INSERT INTO Settings(Id, Value) VALUES(1, $value)
            ON CONFLICT(Id) DO UPDATE SET Value = excluded.Value;
            """, ("$value", json));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<string> GetOutputHistory(long projectId, int limit)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, """
            SELECT OutputPath FROM OutputHistory WHERE ProjectId = $project ORDER BY Id DESC LIMIT $limit;
            """, ("$project", projectId), ("$limit", Math.Max(0, limit)));
        using var reader = command.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read())
            paths.Add(reader.GetString(0));
        return paths;
    }

    public void AddOutputHistory(long projectId, string outputPath, int limit)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        outputPath = outputPath.Trim();
        limit = Math.Max(0, limit);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        if (outputPath.Length > 0 && limit > 0)
        {
            using (var remove = Command(connection, transaction,
                "DELETE FROM OutputHistory WHERE ProjectId = $project AND OutputPath = $path;",
                ("$project", projectId), ("$path", outputPath)))
                remove.ExecuteNonQuery();
            using var insert = Command(connection, transaction,
                "INSERT INTO OutputHistory(ProjectId, OutputPath) VALUES($project, $path);",
                ("$project", projectId), ("$path", outputPath));
            insert.ExecuteNonQuery();
        }
        using (var trim = Command(connection, transaction, """
            DELETE FROM OutputHistory WHERE ProjectId = $project AND Id NOT IN (
                SELECT Id FROM OutputHistory WHERE ProjectId = $project ORDER BY Id DESC LIMIT $limit
            );
            """, ("$project", projectId), ("$limit", limit)))
            trim.ExecuteNonQuery();
        transaction.Commit();
    }

    public string? GetState(string key)
    {
        key = RequireText(key, nameof(key), "界面状态键不能为空。");
        using var connection = OpenConnection();
        using var command = Command(connection, null, "SELECT Value FROM UiState WHERE Key = $key;", ("$key", key));
        return command.ExecuteScalar() as string;
    }

    public void SetState(string key, string value)
    {
        key = RequireText(key, nameof(key), "界面状态键不能为空。");
        ArgumentNullException.ThrowIfNull(value);
        using var connection = OpenConnection();
        using var command = Command(connection, null, """
            INSERT INTO UiState(Key, Value) VALUES($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """, ("$key", key), ("$value", value));
        command.ExecuteNonQuery();
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    private void MoveOrderedItem(long id, int delta, bool favorite)
    {
        using var connection = OpenConnection();
        if (delta == 0)
            return;
        using var transaction = connection.BeginTransaction();
        var items = new List<(long Id, int Order)>();
        var selectSql = favorite
            ? "SELECT Id, SortOrder FROM Favorites WHERE FolderId = (SELECT FolderId FROM Favorites WHERE Id = $id) ORDER BY SortOrder, Id;"
            : "SELECT Id, SortOrder FROM FavoriteFolders WHERE WorkspaceId = (SELECT WorkspaceId FROM FavoriteFolders WHERE Id = $id) ORDER BY SortOrder, Id;";
        using (var select = Command(connection, transaction, selectSql, ("$id", id)))
        {
            using var reader = select.ExecuteReader();
            while (reader.Read())
                items.Add((reader.GetInt64(0), reader.GetInt32(1)));
        }
        var source = items.FindIndex(item => item.Id == id);
        if (source < 0)
            return;
        var destination = (int)Math.Clamp((long)source + delta, 0, items.Count - 1);
        if (source == destination)
            return;

        using var update = Command(connection, transaction, favorite
            ? "UPDATE Favorites SET SortOrder = $order WHERE Id = $id;"
            : "UPDATE FavoriteFolders SET SortOrder = $order WHERE Id = $id;",
            ("$order", 0), ("$id", 0L));
        var step = source < destination ? 1 : -1;
        for (var index = source; index != destination; index += step)
        {
            update.Parameters["$id"].Value = items[index + step].Id;
            update.Parameters["$order"].Value = items[index].Order;
            update.ExecuteNonQuery();
        }
        update.Parameters["$id"].Value = id;
        update.Parameters["$order"].Value = items[destination].Order;
        update.ExecuteNonQuery();
        transaction.Commit();
    }

    private SqliteConnection OpenConnection()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            // SQLite's built-in NOCASE only handles ASCII; Windows paths and folder names also need Unicode casing.
            connection.CreateCollation(CaseInsensitiveCollation,
                static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command;
    }

    private static WorkspaceRecord ReadWorkspace(SqliteDataReader reader) =>
        new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2));

    private static ProjectRecord ReadProject(SqliteDataReader reader, int offset = 0) =>
        new(reader.GetInt64(offset), reader.GetInt64(offset + 1), reader.GetString(offset + 2),
            reader.GetString(offset + 3), reader.GetString(offset + 4));

    private static string RequireText(string value, string parameterName, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(message, parameterName);
        return value.Trim();
    }

    private static bool IsPreserved(string projectPath, HashSet<string> preservedPaths)
    {
        foreach (var path in preservedPaths)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(projectPath, path))
                return true;
            if (projectPath.Length > path.Length && projectPath.StartsWith(path, StringComparison.OrdinalIgnoreCase)
                && (Path.EndsInDirectorySeparator(path) || projectPath[path.Length] == Path.DirectorySeparatorChar
                    || projectPath[path.Length] == Path.AltDirectorySeparatorChar))
                return true;
        }
        return false;
    }
}
