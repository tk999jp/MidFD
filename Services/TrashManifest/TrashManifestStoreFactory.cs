using System.Text.Json;

namespace MidFD.Services.TrashManifestStore;

internal static class TrashManifestStoreFactory
{
    public static ITrashManifestStore CreateJsonStore(string manifestPath, JsonSerializerOptions jsonOptions)
    {
        return new JsonTrashManifestStore(manifestPath, jsonOptions);
    }

    public static ITrashManifestStore CreateSqliteStore(string dbPath)
    {
        return new SqliteTrashManifestStore(dbPath);
    }

    public static ITrashManifestStore CreateStore(ref Configuration.ManagedTrashStoreMode mode, string jsonPath, string sqlitePath, JsonSerializerOptions jsonOptions)
    {
        if (mode == Configuration.ManagedTrashStoreMode.Sqlite)
        {
            try
            {
                var store = new SqliteTrashManifestStore(sqlitePath);
                LogService.Info($"[MidFdTrashStore] ActiveStore=Sqlite DbPath={sqlitePath}");
                return store;
            }
            catch (Exception ex)
            {
                LogService.Error($"[MidFdTrashStore] SQLite initialization failed. Falling back to JSON. path={sqlitePath}, error={ex.Message}");
                mode = Configuration.ManagedTrashStoreMode.Json;
            }
        }

        LogService.Info($"[MidFdTrashStore] ActiveStore=Json ManifestPath={jsonPath}");
        return new JsonTrashManifestStore(jsonPath, jsonOptions);
    }
}
