using System.Threading;

namespace MidFD.Services;

public sealed class DirectoryCountAuditGate
{
    private int _active;

    public bool TryEnter() => Interlocked.Exchange(ref _active, 1) == 0;

    public void Exit() => Volatile.Write(ref _active, 0);
}
