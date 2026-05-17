using System.IO;

namespace MidFD.Models;

public class DirectoryResult
{
    public List<DirectoryInfo> SelectedDirs { get; set; } = new();
    public List<FileInfo> SelectedFiles { get; set; } = new();
}
