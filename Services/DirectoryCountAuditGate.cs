namespace MidFD.Services;

public sealed class DirectoryCountAuditGate
{
    private readonly object _sync = new();
    private Guid? _owner;
    private Guid? _legacyOwner;

    public bool TryEnter()
    {
        lock (_sync)
        {
            if (_owner.HasValue)
            {
                return false;
            }

            Guid owner = Guid.NewGuid();
            _owner = owner;
            _legacyOwner = owner;
            return true;
        }
    }

    public bool TryEnter(Guid owner)
    {
        if (owner == Guid.Empty)
        {
            return false;
        }

        lock (_sync)
        {
            if (_owner.HasValue)
            {
                return false;
            }

            _owner = owner;
            _legacyOwner = null;
            return true;
        }
    }

    public void Exit()
    {
        lock (_sync)
        {
            if (_legacyOwner.HasValue && _owner == _legacyOwner)
            {
                _owner = null;
                _legacyOwner = null;
            }
        }
    }

    public bool Exit(Guid owner)
    {
        lock (_sync)
        {
            if (_owner != owner)
            {
                return false;
            }

            _owner = null;
            _legacyOwner = null;
            return true;
        }
    }
}
