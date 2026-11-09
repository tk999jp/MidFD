using System.Reflection;
using System.Runtime.Loader;

namespace MidFD;

internal static class FullVideoPlaybackExtensionLoader
{
    private static readonly object Gate = new();
    private static Assembly? _assembly;
    private static bool _attempted;

    internal static bool IsAvailable => ReadProperty<bool>("IsAvailable") == true;

    internal static IVideoPlaybackBackend? CreateBackend(string? requestedBackend)
        => Invoke<IVideoPlaybackBackend>("CreateBackend", requestedBackend);

    internal static IVideoCodecProbe? CreateCodecProbe()
        => Invoke<IVideoCodecProbe>("CreateCodecProbe");

    private static T? ReadProperty<T>(string propertyName)
    {
        try
        {
            Assembly? assembly = ResolveAssembly();
            Type? extensionType = assembly?.GetType("MidFD.FullVideoPlaybackExtension", throwOnError: false);
            PropertyInfo? property = extensionType?.GetProperty(
                propertyName,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return property?.GetValue(null) is T result ? result : default;
        }
        catch (Exception error)
        {
            Services.LogService.Warn($"[MidPlayer.Full] optional extension unavailable: {error.GetBaseException().Message}");
            return default;
        }
    }

    private static T? Invoke<T>(string methodName, params object?[] arguments)
    {
        try
        {
            Assembly? assembly = ResolveAssembly();
            Type? extensionType = assembly?.GetType("MidFD.FullVideoPlaybackExtension", throwOnError: false);
            MethodInfo? method = extensionType?.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return method?.Invoke(null, arguments) is T result ? result : default;
        }
        catch (Exception error)
        {
            Services.LogService.Warn($"[MidPlayer.Full] optional extension unavailable: {error.GetBaseException().Message}");
            return default;
        }
    }

    private static Assembly? ResolveAssembly()
    {
        lock (Gate)
        {
            if (_attempted) return _assembly;
            _attempted = true;
            string path = Path.Combine(AppContext.BaseDirectory, "MidPlayer.Full.dll");
            if (!File.Exists(path)) return null;
            try { _assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path)); }
            catch (Exception error)
            {
                Services.LogService.Warn($"[MidPlayer.Full] optional extension load failed: {error.GetBaseException().Message}");
            }
            return _assembly;
        }
    }
}
