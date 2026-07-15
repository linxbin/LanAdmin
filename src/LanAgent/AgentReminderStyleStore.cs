using System.Drawing;
using LanAdmin.Contracts;

namespace LanAgent;

internal sealed record AgentReminderStyleState(
    ReminderStyleDto Style,
    string? BackgroundImagePath,
    string? BackgroundImageUrl,
    DateTimeOffset StyleUpdatedAt);

internal static class AgentReminderStyleStore
{
    private static readonly string StatePath = Path.Combine(AgentStoragePaths.AgentDirectory, "reminder-style.json");

    public static AgentReminderStyleState Load()
    {
        if (!File.Exists(StatePath))
        {
            return new AgentReminderStyleState(ReminderStyleDefaults.CreateDefault(), null, null, DateTimeOffset.UnixEpoch);
        }

        try
        {
            using var stream = File.Open(StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return System.Text.Json.JsonSerializer.Deserialize<AgentReminderStyleState>(stream, AgentJson.Options)
                   ?? new AgentReminderStyleState(ReminderStyleDefaults.CreateDefault(), null, null, DateTimeOffset.UnixEpoch);
        }
        catch
        {
            return new AgentReminderStyleState(ReminderStyleDefaults.CreateDefault(), null, null, DateTimeOffset.UnixEpoch);
        }
    }

    public static void Save(ReminderStyleDto style, string? backgroundImagePath)
    {
        var state = new AgentReminderStyleState(
            style,
            string.IsNullOrWhiteSpace(backgroundImagePath) ? null : backgroundImagePath,
            string.IsNullOrWhiteSpace(style.BackgroundImageUrl) ? null : style.BackgroundImageUrl.Trim(),
            style.UpdatedAt);
        var json = System.Text.Json.JsonSerializer.Serialize(state, AgentJson.Options);
        JsonFileWriter.WriteAtomically(StatePath, json);
    }
}

internal static class AgentReminderBackgroundImageCache
{
    private const long MaxImageBytes = 2 * 1024 * 1024;
    private static readonly string CacheDirectory = Path.Combine(AgentStoragePaths.AgentDirectory, "reminder-assets");
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public static async Task<string?> RefreshAsync(
        ReminderStyleDto style,
        string serverBaseUrl,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var imageUrl = style.BackgroundImageUrl?.Trim();
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        var existing = AgentReminderStyleStore.Load();
        if (string.Equals(existing.BackgroundImageUrl, imageUrl, StringComparison.OrdinalIgnoreCase) &&
            existing.StyleUpdatedAt == style.UpdatedAt &&
            !string.IsNullOrWhiteSpace(existing.BackgroundImagePath) &&
            File.Exists(existing.BackgroundImagePath))
        {
            return existing.BackgroundImagePath;
        }

        if (!TryBuildImageUri(imageUrl, serverBaseUrl, out var imageUri))
        {
            logger.LogWarning("Reminder background image URL is invalid: {ImageUrl}", imageUrl);
            return ExistingImagePathOrNull(existing);
        }

        try
        {
            using var response = await HttpClient.GetAsync(imageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is > MaxImageBytes)
            {
                logger.LogWarning("Reminder background image is too large: {Bytes} bytes", response.Content.Headers.ContentLength);
                return ExistingImagePathOrNull(existing);
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            await CopyBoundedAsync(source, buffer, MaxImageBytes, cancellationToken);
            buffer.Position = 0;

            using (Image.FromStream(buffer, useEmbeddedColorManagement: false, validateImageData: true))
            {
                // Validate image data before caching it.
            }

            var extension = GetExtension(response.Content.Headers.ContentType?.MediaType, imageUri);
            Directory.CreateDirectory(CacheDirectory);
            var targetPath = Path.Combine(CacheDirectory, "reminder-background" + extension);
            var tempPath = targetPath + ".tmp";
            await File.WriteAllBytesAsync(tempPath, buffer.ToArray(), cancellationToken);

            if (File.Exists(targetPath))
            {
                File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, targetPath);
            }

            return targetPath;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or ArgumentException)
        {
            logger.LogWarning(ex, "Failed to cache reminder background image from {ImageUrl}", imageUrl);
            return ExistingImagePathOrNull(existing);
        }
    }

    private static bool TryBuildImageUri(string imageUrl, string serverBaseUrl, out Uri imageUri)
    {
        if (Uri.TryCreate(imageUrl, UriKind.Absolute, out imageUri!))
        {
            return imageUri.Scheme is "http" or "https";
        }

        if (!Uri.TryCreate(serverBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
        {
            return false;
        }

        return Uri.TryCreate(baseUri, imageUrl.TrimStart('/'), out imageUri!);
    }

    private static string? ExistingImagePathOrNull(AgentReminderStyleState existing)
    {
        return !string.IsNullOrWhiteSpace(existing.BackgroundImagePath) && File.Exists(existing.BackgroundImagePath)
            ? existing.BackgroundImagePath
            : null;
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                return;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new IOException($"Image exceeds the maximum allowed size of {maxBytes} bytes.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static string GetExtension(string? mediaType, Uri uri)
    {
        return mediaType?.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            _ => Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => ".jpg",
                ".png" => ".png",
                ".gif" => ".gif",
                ".bmp" => ".bmp",
                _ => ".img"
            }
        };
    }
}
