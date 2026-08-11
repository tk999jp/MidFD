using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace MidFD.Services;

public static class BrowserDroppedImageDownloadService
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TotalDownloadTimeout = TimeSpan.FromSeconds(120);

    public static string DownloadToDirectory(
        Uri imageUri,
        string directoryPath,
        string? suggestedFileName = null,
        DateTime? now = null,
        CancellationToken cancellationToken = default)
    {
        return DownloadToDirectoryCore(imageUri, directoryPath, suggestedFileName, now, handlerFactory: null, dnsResolver: null, cancellationToken);
    }

    internal static string DownloadToDirectoryCore(
        Uri imageUri,
        string directoryPath,
        string? suggestedFileName = null,
        DateTime? now = null,
        Func<IPAddress[], HttpMessageHandler>? handlerFactory = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directoryPath);
        string tempPath = Path.Combine(directoryPath, $".midfd-imgdrop-{Guid.NewGuid():N}.tmp");
        bool finalized = false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TotalDownloadTimeout);
        CancellationToken combinedToken = cts.Token;

        Uri currentUri = imageUri;
        int hops = 0;

        try
        {
            while (hops <= NetworkSecurityPolicyService.MaxRedirectHops)
            {
                combinedToken.ThrowIfCancellationRequested();

                IPAddress[] addresses = NetworkSecurityPolicyService.ResolveAndValidatePublicAddressesAsync(currentUri, combinedToken, dnsResolver)
                    .GetAwaiter().GetResult();
                if (addresses.Length == 0)
                {
                    throw new InvalidOperationException($"非パブリックまたは無効なURL/IPアドレスです: {currentUri}");
                }

                HttpMessageHandler handler = handlerFactory?.Invoke(addresses) ?? CreateSafeHandler(addresses, ConnectTimeout);
                using HttpClient client = new(handler, disposeHandler: true);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("MidFD/1.0");

                HttpRequestMessage request = new(HttpMethod.Get, currentUri);
                using HttpResponseMessage response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, combinedToken)
                    .GetAwaiter().GetResult();

                if (IsRedirectStatusCode(response.StatusCode))
                {
                    hops++;
                    if (hops > NetworkSecurityPolicyService.MaxRedirectHops)
                    {
                        throw new InvalidOperationException($"リダイレクト上限 ({NetworkSecurityPolicyService.MaxRedirectHops} hops) を超過しました。");
                    }

                    Uri? location = response.Headers.Location;
                    if (location == null)
                    {
                        throw new InvalidOperationException("リダイレクトレスポンスに Location ヘッダーがありません。");
                    }

                    Uri nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                    currentUri = nextUri;
                    continue;
                }

                response.EnsureSuccessStatusCode();

                EnsureImageContent(response.Content.Headers.ContentType, currentUri);

                string extension = ResolveExtension(response.Content.Headers.ContentType, currentUri, suggestedFileName);
                if (!IsSupportedImageExtension(extension))
                {
                    throw new InvalidOperationException("画像URLを特定できませんでした。");
                }

                long? contentLength = response.Content.Headers.ContentLength;
                if (contentLength.HasValue && contentLength.Value > NetworkSecurityPolicyService.MaxDownloadSizeBytes)
                {
                    throw new InvalidOperationException($"レスポンス容量制限 ({NetworkSecurityPolicyService.MaxDownloadSizeBytes / 1024 / 1024} MiB) を超過しています: {contentLength.Value} bytes");
                }

                using (Stream input = response.Content.ReadAsStream(combinedToken))
                using (FileStream output = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[8192];
                    long totalBytesRead = 0;
                    int bytesRead;
                    while ((bytesRead = input.ReadAsync(buffer, 0, buffer.Length, combinedToken).GetAwaiter().GetResult()) > 0)
                    {
                        totalBytesRead += bytesRead;
                        if (totalBytesRead > NetworkSecurityPolicyService.MaxDownloadSizeBytes)
                        {
                            throw new InvalidOperationException($"レスポンス容量制限 ({NetworkSecurityPolicyService.MaxDownloadSizeBytes / 1024 / 1024} MiB) を超過しました。");
                        }
                        output.Write(buffer, 0, bytesRead);
                    }
                }

                string fileName = ResolveFileName(currentUri, suggestedFileName, extension, now ?? DateTime.Now);
                string fullPath = Path.Combine(directoryPath, fileName);
                string uniquePath = File.Exists(fullPath) || Directory.Exists(fullPath)
                    ? FileOperationService.GetUniquePathStartingAtOne(fullPath)
                    : fullPath;

                File.Move(tempPath, uniquePath, overwrite: false);
                finalized = true;

                return uniquePath;
            }

            throw new InvalidOperationException("安全なリダイレクト処理を実行できませんでした。");
        }
        catch (OperationCanceledException ex)
        {
            if (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"ダウンロード処理がタイムアウト ({TotalDownloadTimeout.TotalSeconds}秒) しました。", ex);
            }
            throw;
        }
        finally
        {
            if (!finalized && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or (HttpStatusCode)308;
    }

    private static HttpMessageHandler CreateSafeHandler(IPAddress[] verifiedAddresses, TimeSpan connectTimeout)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            Proxy = null,
            ConnectTimeout = connectTimeout,
            ConnectCallback = async (context, cancellationToken) =>
            {
                Exception? lastException = null;
                foreach (IPAddress ip in verifiedAddresses)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Socket socket = new(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };

                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (OperationCanceledException)
                    {
                        socket.Dispose();
                        throw;
                    }
                    catch (Exception ex)
                    {
                        socket.Dispose();
                        lastException = ex;
                    }
                }

                throw new HttpRequestException("検証済みIPアドレスへの接続にすべて失敗しました。", lastException);
            }
        };
    }

    public static bool IsSupportedImageExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveExtension(MediaTypeHeaderValue? contentType, Uri imageUri, string? suggestedFileName)
    {
        string ext = Path.GetExtension(suggestedFileName ?? string.Empty);
        if (IsSupportedImageExtension(ext))
        {
            return ext;
        }

        ext = Path.GetExtension(imageUri.AbsolutePath);
        if (IsSupportedImageExtension(ext))
        {
            return ext;
        }

        string mediaType = contentType?.MediaType ?? string.Empty;
        return mediaType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            _ => string.Empty,
        };
    }

    private static void EnsureImageContent(MediaTypeHeaderValue? contentType, Uri imageUri)
    {
        string mediaType = contentType?.MediaType ?? string.Empty;
        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            && IsSupportedImageExtension(Path.GetExtension(imageUri.AbsolutePath)))
        {
            return;
        }

        throw new InvalidOperationException($"画像レスポンスではありません: {mediaType}");
    }

    private static string ResolveFileName(Uri imageUri, string? suggestedFileName, string extension, DateTime now)
    {
        string candidate = suggestedFileName ?? Path.GetFileName(imageUri.AbsolutePath);
        candidate = SanitizeFileName(candidate);

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = $"DroppedUrl_{now:yyyyMMdd_HHmmss}{extension}";
        }

        string candidateExt = Path.GetExtension(candidate);
        if (!IsSupportedImageExtension(candidateExt))
        {
            candidate = Path.GetFileNameWithoutExtension(candidate) + extension;
        }

        if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(candidate)))
        {
            candidate = $"DroppedUrl_{now:yyyyMMdd_HHmmss}{extension}";
        }

        return candidate;
    }

    private static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        return new string(fileName
            .Trim()
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray());
    }
}
