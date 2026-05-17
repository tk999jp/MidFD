using System.Runtime.InteropServices;

namespace MidFD.Models;

public class NaturalStringComparer : IComparer<string>
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);

    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
}
