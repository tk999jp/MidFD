using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace MidFD.Services.Susie;

public static class SusiePreviewService
{
    private static readonly List<SusiePlugin> _plugins = new();
    private static bool _initialized = false;
    private static readonly object _initLock = new();
    private static readonly string[] _imageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".tga", ".pic", ".mag", ".pi", ".eri", ".heic", ".avif" };

    public static void Initialize()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;

            bool is64Bit = Environment.Is64BitProcess;
            LogService.Info($"[Susie] Initialize() Start. Is64BitProcess: {is64Bit}");
            Debug.WriteLine($"[Susie] Initialize() Start. Is64BitProcess: {is64Bit}");

        string pluginDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");
        if (!Directory.Exists(pluginDir))
        {
            LogService.Warn($"[Susie] Plugin dir not found: {pluginDir}");
            Debug.WriteLine($"[Susie] Plugin dir not found: {pluginDir}");
            return;
        }

            foreach (var file in Directory.GetFiles(pluginDir, "*.sph"))
            {
                var plugin = new SusiePlugin(file);
                if (plugin.IsLoaded)
                {
                    _plugins.Add(plugin);
                }
            }
            _initialized = true;
        }
    }

    public static (Bitmap? Image, string ErrorMessage) GetPreviewImage(string path)
    {
        if (!_initialized) Initialize();

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(_imageExtensions, ext) < 0)
        {
            return (null, "画像形式ではありません");
        }

        if (_plugins.Count == 0)
        {
            return (null, "Susieプラグイン(.sph)がありません");
        }

        foreach (var plugin in _plugins)
        {
            Debug.WriteLine($"[Susie] Call IsSupported: {path}");
            if (plugin.IsSupported(path))
            {
                Debug.WriteLine("[Susie] IsSupported -> True");
                try
                {
                    Debug.WriteLine($"[Susie] Call GetPreviewOrPicture...");
                    var bmp = plugin.GetPreviewOrPicture(path);
                    if (bmp != null)
                    {
                        Debug.WriteLine($"[Susie] Decoded bitmap successfully. [{bmp.Width}x{bmp.Height}]");
                        return (bmp, "");
                    }
                    Debug.WriteLine("[Susie] GetPreviewOrPicture returned null.");
                }
                catch (Exception ex)
                {
                    LogService.Error($"[Susie] デコード失敗 ({path})", ex);
                    Debug.WriteLine($"[Susie] デコード失敗 ({path}): {ex.Message}");
                    return (null, $"デコード失敗: {ex.Message}");
                }
            }
            else
            {
                Debug.WriteLine("[Susie] IsSupported -> False");
            }
        }

        return (null, "対応するプラグインがないか、デコードに失敗しました");
    }

    private class SusiePlugin : IDisposable
    {
        private IntPtr _hModule;
        private NativeMethods.IsSupportedDelegate? _isSupported;
        private NativeMethods.GetPreviewDelegate? _getPreview;
        private NativeMethods.GetPictureDelegate? _getPicture;

        public bool IsLoaded => _hModule != IntPtr.Zero && _isSupported != null && (_getPreview != null || _getPicture != null);

        public SusiePlugin(string dllPath)
        {
            Debug.WriteLine($"[SusiePlugin] Loading: {dllPath}");
            _hModule = NativeMethods.LoadLibrary(dllPath);
            if (_hModule != IntPtr.Zero)
            {
                Debug.WriteLine($"[SusiePlugin] LoadLibrary OK: {dllPath}");
                var pIsSupported = NativeMethods.GetProcAddress(_hModule, "IsSupported");
                if (pIsSupported != IntPtr.Zero)
                    _isSupported = Marshal.GetDelegateForFunctionPointer<NativeMethods.IsSupportedDelegate>(pIsSupported);

                var pGetPreview = NativeMethods.GetProcAddress(_hModule, "GetPreview");
                if (pGetPreview != IntPtr.Zero)
                    _getPreview = Marshal.GetDelegateForFunctionPointer<NativeMethods.GetPreviewDelegate>(pGetPreview);

                var pGetPicture = NativeMethods.GetProcAddress(_hModule, "GetPicture");
                if (pGetPicture != IntPtr.Zero)
                    _getPicture = Marshal.GetDelegateForFunctionPointer<NativeMethods.GetPictureDelegate>(pGetPicture);
                
                Debug.WriteLine($"[SusiePlugin] Resolves: IsSupported={pIsSupported != IntPtr.Zero}, GetPreview={pGetPreview != IntPtr.Zero}, GetPicture={pGetPicture != IntPtr.Zero}");
            }
            else
            {
                Debug.WriteLine($"[SusiePlugin] LoadLibrary FAILED: {dllPath}");
            }
        }

        public bool IsSupported(string filename)
        {
            if (_isSupported == null) return false;
            return _isSupported(filename, IntPtr.Zero) != 0;
        }

        public Bitmap? GetPreviewOrPicture(string filename)
        {
            IntPtr hInfo = IntPtr.Zero;
            IntPtr hBm = IntPtr.Zero;
            int ret = -1;

            if (_getPreview != null)
            {
                Debug.WriteLine("[Susie] API Invoke -> GetPreview()");
                ret = _getPreview(filename, filename.Length, 0, out hInfo, out hBm, IntPtr.Zero, IntPtr.Zero);
                Debug.WriteLine($"[Susie] GetPreview() returned: {ret}");
            }
            if (ret != 0 && _getPicture != null)
            {
                Debug.WriteLine("[Susie] API Invoke -> GetPicture()");
                ret = _getPicture(filename, filename.Length, 0, out hInfo, out hBm, IntPtr.Zero, IntPtr.Zero);
                Debug.WriteLine($"[Susie] GetPicture() returned: {ret}");
            }

            if (ret != 0 || hInfo == IntPtr.Zero || hBm == IntPtr.Zero)
            {
                if (hInfo != IntPtr.Zero) NativeMethods.LocalFree(hInfo);
                if (hBm != IntPtr.Zero) NativeMethods.LocalFree(hBm);
                return null;
            }

            try
            {
                return ConvertSusieDIBToBitmap(hInfo, hBm);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Susie] Bitmap変換例外: {ex.Message}");
                return null;
            }
            finally
            {
                // ConvertSusieDIBToBitmap内部で例外が起きても起きなくても、ここで大元のDIBハンドルを解放する
                if (hInfo != IntPtr.Zero) NativeMethods.LocalFree(hInfo);
                if (hBm != IntPtr.Zero) NativeMethods.LocalFree(hBm);
            }
        }

        private Bitmap? ConvertSusieDIBToBitmap(IntPtr hInfo, IntPtr hBm)
        {
            Debug.WriteLine("[Susie] ConvertSusieDIBToBitmap Start");
            IntPtr pInfo = NativeMethods.LocalLock(hInfo);
            IntPtr pBm = NativeMethods.LocalLock(hBm);

            if (pInfo == IntPtr.Zero || pBm == IntPtr.Zero)
            {
                Debug.WriteLine("[Susie] LocalLock Failed");
                if (pInfo != IntPtr.Zero) NativeMethods.LocalUnlock(hInfo);
                if (pBm != IntPtr.Zero) NativeMethods.LocalUnlock(hBm);
                return null;
            }

            try
            {
                int infoSize = (int)NativeMethods.LocalSize(hInfo);
                int bmSize = (int)NativeMethods.LocalSize(hBm);
                int totalSize = 14 + infoSize + bmSize;
                
                byte[] buf = new byte[totalSize];

                // BITMAPFILEHEADER
                buf[0] = 0x42; // B
                buf[1] = 0x4D; // M
                BitConverter.GetBytes(totalSize).CopyTo(buf, 2); // bfSize
                BitConverter.GetBytes(14 + infoSize).CopyTo(buf, 10); // bfOffBits

                Marshal.Copy(pInfo, buf, 14, infoSize);
                Marshal.Copy(pBm, buf, 14 + infoSize, bmSize);

                Debug.WriteLine("[Susie] ConvertSusieDIBToBitmap Finish");
                using var ms = new MemoryStream(buf);
                return new Bitmap(ms);
            }
            finally
            {
                if (pInfo != IntPtr.Zero) NativeMethods.LocalUnlock(hInfo);
                if (pBm != IntPtr.Zero) NativeMethods.LocalUnlock(hBm);
            }
        }

        public void Dispose()
        {
            if (_hModule != IntPtr.Zero)
            {
                NativeMethods.FreeLibrary(_hModule);
                _hModule = IntPtr.Zero;
            }
        }
    }
}
