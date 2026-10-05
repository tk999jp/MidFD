namespace MidFD.Helpers;

public sealed class BrowserNamePrefixJumpSession
{
    public bool IsActive { get; private set; }
    public string Prefix { get; private set; } = string.Empty;

    public void Begin()
    {
        IsActive = true;
        Prefix = string.Empty;
    }

    public bool Append(char value)
    {
        if (!IsActive || Prefix.Length != 0 || char.IsControl(value))
        {
            return false;
        }

        Prefix = value.ToString();
        return true;
    }

    public bool Backspace()
    {
        if (!IsActive || Prefix.Length == 0)
        {
            return false;
        }

        Prefix = Prefix[..^1];
        return true;
    }

    public bool End()
    {
        if (!IsActive)
        {
            return false;
        }

        IsActive = false;
        Prefix = string.Empty;
        return true;
    }
}
