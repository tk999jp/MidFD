using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using System.Drawing;
using MidFD.Configuration;
using MidFD.Services;

namespace MidFD;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && string.Equals(args[0], "--susie-verify", StringComparison.Ordinal))
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return RunSusieVerification(args[1]);
        }

        if (args.Length == 2 && string.Equals(args[0], "--susie-inspect", StringComparison.Ordinal))
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return RunSusieInspection(args[1]);
        }

        ApplicationConfiguration.Initialize();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        if (TryReadHostArguments(args, out string? pipeName, out Guid viewerId, out ImageViewerSettings settings, out bool allowQuantization, out bool allowSvgClipboard))
        {
            RunHosted(pipeName!, viewerId, settings, allowQuantization, allowSvgClipboard);
            return 0;
        }

        ImageViewerSettings standaloneSettings = ImageViewerSettingsLoader.LoadStandalone();
        var standalone = new ImageViewerForm(standaloneSettings);
        string? standaloneImagePath = args.Length == 1 ? args[0] : null;
        if (!string.IsNullOrWhiteSpace(standaloneImagePath))
        {
            standalone.LoadMedia(standaloneImagePath, ImageViewerMediaKind.Image);
        }
        Application.Run(standalone);
        return 0;
    }

    private static int RunSusieVerification(string manifestPath)
    {
        try
        {
            string fullManifestPath = Path.GetFullPath(manifestPath);
            SusieVerificationManifest manifest = JsonSerializer.Deserialize<SusieVerificationManifest>(
                File.ReadAllText(fullManifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("Verification manifest is empty.");
            if (manifest.Plugins.Count == 0 || manifest.Cases.Count == 0)
                throw new InvalidDataException("The verification manifest requires at least one Plugin and one case.");

            var settings = new SusiePluginSettings
            {
                Plugins = manifest.Plugins.Select(path => new SusiePluginRegistration { PluginPath = path, IsEnabled = true }).ToList()
            };
            foreach (SusieVerificationCase item in manifest.Cases)
            {
                string extension = SusiePluginSettings.NormalizeExtension(Path.GetExtension(item.ImagePath));
                if (item.ForceStandardDecoder)
                {
                    settings.DecoderByExtension[extension] = "standard";
                }
                else if (!string.IsNullOrWhiteSpace(item.PluginPath))
                {
                    settings.DecoderByExtension[extension] = Path.GetFullPath(item.PluginPath);
                }
                if (!item.AllowStandardFallback)
                {
                    settings.DisabledStandardExtensions.Add(extension);
                }
                if (!string.IsNullOrWhiteSpace(item.RetryImagePath) && !string.IsNullOrWhiteSpace(item.PluginPath))
                {
                    settings.DecoderByExtension[SusiePluginSettings.NormalizeExtension(Path.GetExtension(item.RetryImagePath))]
                        = Path.GetFullPath(item.PluginPath);
                }
            }
            settings.Normalize();

            using var decoder = new SusieDecoderService(settings);
            var results = new List<object>();
            foreach (string pluginPath in manifest.Plugins)
            {
                SusiePluginRuntimeMetadata? metadata = decoder.InspectPluginAsync(pluginPath, CancellationToken.None).GetAwaiter().GetResult();
                results.Add(new
                {
                    type = "metadata",
                    pluginPath = Path.GetFullPath(pluginPath),
                    success = metadata != null,
                    displayName = metadata?.DisplayName,
                    versionInfo = metadata?.VersionInfo,
                    extensions = metadata?.Extensions,
                    formatNames = metadata?.FormatNames,
                    error = metadata == null ? decoder.GetHostFailure(pluginPath) : null
                });
            }

            var caseResults = new List<object>();
            foreach (SusieVerificationCase item in manifest.Cases)
            {
                var timer = Stopwatch.StartNew();
                using var timeout = new CancellationTokenSource();
                if (item.CancelAfterMilliseconds is > 0) timeout.CancelAfter(item.CancelAfterMilliseconds.Value);
                bool cancelled = false;
                bool success = false;
                string error = string.Empty;
                int? width = null;
                int? height = null;
                int? topLeftArgb = null;
                try
                {
                    var result = decoder.DecodeAsync(item.ImagePath, timeout.Token).GetAwaiter().GetResult();
                    error = result.ErrorMessage;
                    using Bitmap? image = result.Image;
                    if (image != null)
                    {
                        success = true;
                        width = image.Width;
                        height = image.Height;
                        topLeftArgb = image.GetPixel(0, 0).ToArgb();
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                timer.Stop();
                caseResults.Add(new
                {
                    type = "case",
                    name = item.Name,
                    imagePath = Path.GetFullPath(item.ImagePath),
                    pluginPath = string.IsNullOrWhiteSpace(item.PluginPath) ? null : Path.GetFullPath(item.PluginPath),
                    forcedStandardDecoder = item.ForceStandardDecoder,
                    success,
                    cancelled,
                    error,
                    width,
                    height,
                    topLeftArgb,
                    elapsedMilliseconds = timer.ElapsedMilliseconds
                });

                if (cancelled && !string.IsNullOrWhiteSpace(item.RetryImagePath))
                {
                    var retryTimer = Stopwatch.StartNew();
                    var retry = decoder.DecodeAsync(item.RetryImagePath, CancellationToken.None).GetAwaiter().GetResult();
                    retryTimer.Stop();
                    using Bitmap? retryImage = retry.Image;
                    caseResults.Add(new
                    {
                        type = "retry",
                        name = item.Name + "-retry",
                        imagePath = Path.GetFullPath(item.RetryImagePath),
                        success = retryImage != null,
                        error = retry.ErrorMessage,
                        width = retryImage?.Width,
                        height = retryImage?.Height,
                        topLeftArgb = retryImage?.GetPixel(0, 0).ToArgb(),
                        elapsedMilliseconds = retryTimer.ElapsedMilliseconds
                    });
                }
            }

            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                success = true,
                processId = Environment.ProcessId,
                is64BitProcess = Environment.Is64BitProcess,
                hostProcessIds = decoder.SnapshotHostProcessIds(),
                results = results.Concat(caseResults)
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                success = false,
                is64BitProcess = Environment.Is64BitProcess,
                error = $"{ex.GetType().Name}: {ex.Message}"
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return 1;
        }
    }

    private static int RunSusieInspection(string pluginPath)
    {
        try
        {
            using var decoder = new SusieDecoderService(new SusiePluginSettings
            {
                Plugins = new List<SusiePluginRegistration>
                {
                    new() { PluginPath = Path.GetFullPath(pluginPath), IsEnabled = true }
                }
            });
            SusiePluginRuntimeMetadata? metadata = decoder.InspectPluginAsync(pluginPath, CancellationToken.None).GetAwaiter().GetResult();
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                success = metadata != null,
                pluginPath = Path.GetFullPath(pluginPath),
                displayName = metadata?.DisplayName,
                versionInfo = metadata?.VersionInfo,
                extensions = metadata?.Extensions,
                formatNames = metadata?.FormatNames,
                error = metadata == null ? decoder.GetHostFailure(pluginPath) : null
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return metadata == null ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                success = false,
                pluginPath,
                error = $"{ex.GetType().Name}: {ex.Message}"
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return 1;
        }
    }

    private static void RunHosted(
        string pipeName,
        Guid viewerId,
        ImageViewerSettings settings,
        bool allowQuantization,
        bool allowSvgClipboard)
    {
        var pipe = new ImageViewerIpcClient(pipeName, viewerId);
        using var form = new ImageViewerForm(settings, allowQuantization, allowSvgClipboard, pipe.TrySendCommand, viewerId);
        _ = form.Handle;
        pipe.AttachForm(form);
        // A MainForm assigned to the main message loop is automatically shown.
        // Keep hosted prewarm genuinely hidden until the explicit IPC show request.
        using var context = new ApplicationContext();
        form.FormClosed += (_, _) => context.ExitThread();
        Application.Run(context);
        pipe.FlushAndStopAsync().GetAwaiter().GetResult();
        pipe.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static bool TryReadHostArguments(
        string[] args,
        out string? pipeName,
        out Guid viewerId,
        out ImageViewerSettings settings,
        out bool allowQuantization,
        out bool allowSvgClipboard)
    {
        pipeName = null;
        viewerId = Guid.Empty;
        settings = new ImageViewerSettings();
        allowQuantization = true;
        allowSvgClipboard = true;

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--host-pipe":
                    pipeName = args[++i];
                    break;
                case "--viewer-id" when Guid.TryParse(args[i + 1], out Guid parsed):
                    viewerId = parsed;
                    i++;
                    break;
                case "--settings-base64":
                    try
                    {
                        string json = Encoding.UTF8.GetString(Convert.FromBase64String(args[++i]));
                        using JsonDocument document = JsonDocument.Parse(json);
                        JsonElement root = document.RootElement;
                        if (root.TryGetProperty("settings", out JsonElement settingsElement))
                        {
                            settings = settingsElement.Deserialize<ImageViewerSettings>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? settings;
                        }
                        allowQuantization = !root.TryGetProperty("allowQuantization", out JsonElement quantization) || quantization.GetBoolean();
                        allowSvgClipboard = !root.TryGetProperty("allowSvgClipboard", out JsonElement svgClipboard) || svgClipboard.GetBoolean();
                    }
                    catch
                    {
                        return false;
                    }
                    break;
            }
        }

        return !string.IsNullOrWhiteSpace(pipeName) && viewerId != Guid.Empty;
    }
}

internal sealed class SusieVerificationManifest
{
    public List<string> Plugins { get; init; } = new();
    public List<SusieVerificationCase> Cases { get; init; } = new();
}

internal sealed class SusieVerificationCase
{
    public string Name { get; init; } = string.Empty;
    public string PluginPath { get; init; } = string.Empty;
    public string ImagePath { get; init; } = string.Empty;
    public int? CancelAfterMilliseconds { get; init; }
    public string? RetryImagePath { get; init; }
    public bool AllowStandardFallback { get; init; }
    public bool ForceStandardDecoder { get; init; }
}
