using DelphiBuilder;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DelphiBuilder.Tests;

public sealed class WorkspaceFavoritesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DelphiBuilder.Tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_root, "test.db");

    public WorkspaceFavoritesTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void FolderNamesAndReorderingAreScopedToWorkspace()
    {
        using var store = new AppStore(DatabasePath);
        var a = store.AddWorkspace(Path.Combine(_root, "A"));
        var b = store.AddWorkspace(Path.Combine(_root, "B"));
        var aFirst = store.AddFavoriteFolder(a.Id, "常用");
        var bFirst = store.AddFavoriteFolder(b.Id, "常用");
        var aSecond = store.AddFavoriteFolder(a.Id, "发布");
        var bSecond = store.AddFavoriteFolder(b.Id, "发布");

        Assert.NotEqual(aFirst.Id, bFirst.Id);
        Assert.Equal(aFirst.Id, store.AddFavoriteFolder(a.Id, "常用").Id);
        store.MoveFavoriteFolder(aSecond.Id, -1);
        Assert.Equal([aSecond.Id, aFirst.Id], store.GetFavoriteFolders(a.Id).Select(folder => folder.Id));
        Assert.Equal([bFirst.Id, bSecond.Id], store.GetFavoriteFolders(b.Id).Select(folder => folder.Id));
        store.MoveFavoriteFolder(aSecond.Id, -1);
        Assert.Equal([aSecond.Id, aFirst.Id], store.GetFavoriteFolders(a.Id).Select(folder => folder.Id));
        Assert.Throws<InvalidOperationException>(() => store.RenameFavoriteFolder(aFirst.Id, "发布"));
    }

    [Fact]
    public void FavoritesCannotReferenceAnotherWorkspaceAndWorkspaceRemovalIsIsolated()
    {
        using var store = new AppStore(DatabasePath);
        var a = AddProject(store, "A", "ProjectA");
        var b = AddProject(store, "B", "ProjectB");
        var aFolder = store.AddFavoriteFolder(a.WorkspaceId, "常用");
        var bFolder = store.AddFavoriteFolder(b.WorkspaceId, "常用");
        store.AddFavorite(aFolder.Id, a.Id);
        store.AddFavorite(aFolder.Id, a.Id);
        store.AddFavorite(bFolder.Id, b.Id);

        Assert.Throws<SqliteException>(() => store.AddFavorite(aFolder.Id, b.Id));
        Assert.Equal(a.Id, Assert.Single(store.GetFavorites(aFolder.Id)).Project.Id);
        Assert.Equal(b.Id, Assert.Single(store.GetFavorites(bFolder.Id)).Project.Id);
        store.RemoveWorkspace(a.WorkspaceId);
        Assert.Empty(store.GetFavoriteFolders(a.WorkspaceId));
        Assert.Empty(store.GetFavorites(aFolder.Id));
        Assert.Equal(bFolder.Id, Assert.Single(store.GetFavoriteFolders(b.WorkspaceId)).Id);
        Assert.Equal(b.Id, Assert.Single(store.GetFavorites(bFolder.Id)).Project.Id);
        Assert.True(File.Exists(a.FilePath));
        Assert.True(File.Exists(b.FilePath));
    }

    [Fact]
    public void LegacyMixedFoldersSplitByProjectOwnershipWithoutLosingOrderOrOtherState()
    {
        CreateLegacyDatabase();
        long clonedFolder;
        using (var store = new AppStore(DatabasePath))
        {
            var aFolder = Assert.Single(store.GetFavoriteFolders(1));
            Assert.Equal("常用", aFolder.Name);
            Assert.Equal(10, aFolder.Id);
            Assert.Equal([100L, 102L], store.GetFavorites(aFolder.Id).Select(item => item.Id));
            Assert.Equal([2L, 1L], store.GetFavorites(aFolder.Id).Select(item => item.Project.Id));
            Assert.Equal(["发布", "常用", "空文件夹"], store.GetFavoriteFolders(2).Select(folder => folder.Name));
            clonedFolder = store.GetFavoriteFolders(2).Single(folder => folder.Name == "常用").Id;
            Assert.NotEqual(aFolder.Id, clonedFolder);
            Assert.Equal(101, Assert.Single(store.GetFavorites(clonedFolder)).Id);
            Assert.Empty(store.GetFavoriteFolders(3));
            Assert.Equal("saved-window-layout", store.GetState("windowLayout"));
            Assert.Equal("utf8", store.LoadSettings().OutputEncoding);
            Assert.Equal(["saved-output"], store.GetOutputHistory(1, 5));
        }
        using var reopened = new AppStore(DatabasePath);
        Assert.Equal(clonedFolder, reopened.GetFavoriteFolders(2).Single(folder => folder.Name == "常用").Id);
        Assert.Equal([100L, 102L], reopened.GetFavorites(10).Select(item => item.Id));
        Assert.Equal(2, reopened.GetProject(3)!.WorkspaceId);
        Assert.Equal("Release", reopened.GetProject(3)!.ActiveConfiguration);
    }

    [Fact]
    public void LegacyEmptyFoldersWithoutAnyWorkspaceAttachOnlyToFirstAddedWorkspace()
    {
        CreateLegacyDatabase(empty: true);
        using (var store = new AppStore(DatabasePath))
        {
            Assert.Empty(store.GetWorkspaces());
            var first = store.AddWorkspace(Path.Combine(_root, "first"));
            Assert.Equal("空文件夹", Assert.Single(store.GetFavoriteFolders(first.Id)).Name);
            var second = store.AddWorkspace(Path.Combine(_root, "second"));
            Assert.Empty(store.GetFavoriteFolders(second.Id));
        }
        using var reopened = new AppStore(DatabasePath);
        Assert.Equal(30, Assert.Single(reopened.GetFavoriteFolders(reopened.GetWorkspaces()[0].Id)).Id);
    }

    [Fact]
    public void FailedLegacyMigrationRollsBackInsteadOfDiscardingProjectReferences()
    {
        CreateLegacyDatabase();
        using (var connection = OpenRawConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO Favorites(Id, FolderId, ProjectId, SortOrder) VALUES(999, 10, 999, 9);";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => new AppStore(DatabasePath));
        using var checkConnection = OpenRawConnection();
        using var check = checkConnection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('FavoriteFolders') WHERE name = 'WorkspaceId';";
        Assert.Equal(0L, check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM Favorites;";
        Assert.Equal(5L, check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('LegacyFavorites', 'LegacyFavoriteFolders');";
        Assert.Equal(0L, check.ExecuteScalar());
    }

    private ProjectRecord AddProject(AppStore store, string directory, string name)
    {
        var path = Path.Combine(_root, directory);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, name + ".dproj"), "<Project />");
        var workspace = store.AddWorkspace(path);
        store.ScanWorkspace(workspace);
        return Assert.Single(store.GetProjects(workspace.Id));
    }

    private SqliteConnection OpenRawConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Pooling = false, ForeignKeys = false
        }.ToString());
        connection.Open();
        connection.CreateCollation("ORDINAL_IGNORE_CASE", StringComparer.OrdinalIgnoreCase.Compare);
        return connection;
    }

    private void CreateLegacyDatabase(bool empty = false)
    {
        using var connection = OpenRawConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Workspaces(Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL,
                RootPath TEXT NOT NULL COLLATE ORDINAL_IGNORE_CASE UNIQUE);
            CREATE TABLE Projects(Id INTEGER PRIMARY KEY AUTOINCREMENT,
                WorkspaceId INTEGER NOT NULL REFERENCES Workspaces(Id) ON DELETE CASCADE,
                FilePath TEXT NOT NULL COLLATE ORDINAL_IGNORE_CASE,
                ActiveConfiguration TEXT NOT NULL DEFAULT '', Platform TEXT NOT NULL DEFAULT 'Win32',
                UNIQUE(WorkspaceId, FilePath));
            CREATE TABLE FavoriteFolders(Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL COLLATE ORDINAL_IGNORE_CASE UNIQUE, SortOrder INTEGER NOT NULL);
            CREATE TABLE Favorites(Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FolderId INTEGER NOT NULL REFERENCES FavoriteFolders(Id) ON DELETE CASCADE,
                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                SortOrder INTEGER NOT NULL, UNIQUE(FolderId, ProjectId));
            CREATE INDEX IX_Favorites_ProjectId ON Favorites(ProjectId);
            CREATE INDEX IX_Favorites_Order ON Favorites(FolderId, SortOrder, Id);
            CREATE TABLE UiState(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            INSERT INTO FavoriteFolders(Id, Name, SortOrder) VALUES(30, '空文件夹', 6);
            """;
        command.ExecuteNonQuery();
        if (empty) return;
        command.CommandText = """
            INSERT INTO Workspaces(Id, Name, RootPath) VALUES(1, 'A', 'A'), (2, 'B', 'B'), (3, 'C', 'C');
            INSERT INTO Projects(Id, WorkspaceId, FilePath, ActiveConfiguration) VALUES
                (1, 1, 'A/ProjectA1.dproj', 'Debug'), (2, 1, 'A/ProjectA2.dproj', 'Debug'),
                (3, 2, 'B/ProjectB.dproj', 'Release');
            INSERT INTO FavoriteFolders(Id, Name, SortOrder) VALUES(10, '常用', 2), (20, '发布', 0);
            INSERT INTO Favorites(Id, FolderId, ProjectId, SortOrder) VALUES
                (100, 10, 2, 2), (101, 10, 3, 4), (102, 10, 1, 8), (103, 20, 3, 0);
            INSERT INTO UiState(Key, Value) VALUES('workspace', '2'), ('windowLayout', 'saved-window-layout');
            CREATE TABLE Settings(Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);
            INSERT INTO Settings(Id, Value) VALUES(1, '{"OutputEncoding":"utf8"}');
            CREATE TABLE OutputHistory(Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                OutputPath TEXT NOT NULL COLLATE ORDINAL_IGNORE_CASE, UNIQUE(ProjectId, OutputPath));
            INSERT INTO OutputHistory(ProjectId, OutputPath) VALUES(1, 'saved-output');
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
