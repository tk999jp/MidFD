namespace MidFD.Services;

/// <summary>Publishes a complete, flushed sibling file; failures never truncate the destination.</summary>
internal static class AtomicFileWriter
{
    internal static void Write(string path, Action<Stream> write,
        Action<string, string>? publish = null)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException($"保存先はReadOnlyです: {fullPath}");
        string temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            if (publish != null) publish(temporary, fullPath);
            else if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { LogService.Warn($"Temporary save cleanup failed: {temporary}: {ex.Message}"); }
        }
    }
}
