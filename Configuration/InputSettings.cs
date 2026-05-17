namespace MidFD.Configuration;

public class InputSettings
{
    public const string StandardProfileValue = "Standard";
    public const string FdCompatibleProfileValue = "FDCompatible";

    public string FunctionKeyProfile { get; set; } = StandardProfileValue;
    public string CommandLauncherShortcut { get; set; } = "Ctrl+Shift+P";
    public bool EnableMouseGestures { get; set; } = true;

    public InputSettings Clone() => (InputSettings)MemberwiseClone();
}
