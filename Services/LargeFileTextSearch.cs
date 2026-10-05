using System.Threading;
using System.Threading.Tasks;
using System.Text;
using MidFD.Models;

namespace MidFD.Services;

internal interface ILargeFileTextSearch
{
    Task<(int Line, int Column, int Length)?> SearchAsync(
        LargeFilePreviewState state,
        string query,
        int startLine,
        int startColumn,
        bool backward,
        Encoding encoding,
        CancellationToken token);
}

internal sealed class LargeFileTextSearch : ILargeFileTextSearch
{
    public Task<(int Line, int Column, int Length)?> SearchAsync(
        LargeFilePreviewState state,
        string query,
        int startLine,
        int startColumn,
        bool backward,
        Encoding encoding,
        CancellationToken token)
        => LargeFileLineReaderService.SearchTextAsync(
            state,
            query,
            startLine,
            startColumn,
            backward,
            encoding,
            token);
}
