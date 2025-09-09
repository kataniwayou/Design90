using System.Text.Json;

namespace Plugin.Shared.Utilities;

/// <summary>
/// Utility class for detecting file types based on extensions and MIME types
/// Provides centralized file type detection logic for use across plugins
/// </summary>
public static class FileTypeDetector
{
    // Audio file type constants
    private static readonly string[] AudioExtensions = { ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma" };
    private static readonly string[] AudioMimeTypes = { "audio/mpeg", "audio/wav", "audio/flac", "audio/aac", "audio/ogg", "audio/mp4", "audio/x-ms-wma" };

    // Text file type constants
    private static readonly string[] TextExtensions = { ".txt", ".md", ".json", ".xml", ".csv", ".log", ".info" };
    private static readonly string[] TextMimeTypes = { "text/plain", "text/markdown", "application/json", "text/xml", "text/csv", "application/xml" };

    // XML file type constants
    private static readonly string[] XmlExtensions = { ".xml" };
    private static readonly string[] XmlMimeTypes = { "text/xml", "application/xml" };

    // Image file type constants
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".webp" };
    private static readonly string[] ImageMimeTypes = { "image/jpeg", "image/png", "image/gif", "image/bmp", "image/tiff", "image/webp" };

    // Video file type constants
    private static readonly string[] VideoExtensions = { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm" };
    private static readonly string[] VideoMimeTypes = { "video/mp4", "video/avi", "video/x-msvideo", "video/quicktime", "video/x-ms-wmv", "video/x-flv", "video/webm" };

    /// <summary>
    /// Check if file is an audio file based on extension and MIME type
    /// </summary>
    /// <param name="fileExtension">File extension (e.g., ".mp3")</param>
    /// <param name="mimeType">MIME type (e.g., "audio/mpeg")</param>
    /// <returns>True if the file is an audio file</returns>
    public static bool IsAudioFile(string fileExtension, string mimeType) =>
        AudioExtensions.Contains(fileExtension?.ToLowerInvariant()) || 
        AudioMimeTypes.Any(mime => mimeType?.ToLowerInvariant().Contains(mime) == true);

    /// <summary>
    /// Check if file is a text file based on extension and MIME type
    /// </summary>
    /// <param name="fileExtension">File extension (e.g., ".txt")</param>
    /// <param name="mimeType">MIME type (e.g., "text/plain")</param>
    /// <returns>True if the file is a text file</returns>
    public static bool IsTextFile(string fileExtension, string mimeType) =>
        TextExtensions.Contains(fileExtension?.ToLowerInvariant()) || 
        TextMimeTypes.Any(mime => mimeType?.ToLowerInvariant().Contains(mime) == true);

    /// <summary>
    /// Check if file is an XML file based on extension and MIME type
    /// </summary>
    /// <param name="fileExtension">File extension (e.g., ".xml")</param>
    /// <param name="mimeType">MIME type (e.g., "text/xml")</param>
    /// <returns>True if the file is an XML file</returns>
    public static bool IsXmlFile(string fileExtension, string mimeType) =>
        XmlExtensions.Contains(fileExtension?.ToLowerInvariant()) || 
        XmlMimeTypes.Any(mime => mimeType?.ToLowerInvariant().Contains(mime) == true);

    /// <summary>
    /// Check if file is an XML file including standardized content detection
    /// </summary>
    /// <param name="fileExtension">File extension (e.g., ".xml")</param>
    /// <param name="mimeType">MIME type (e.g., "text/xml")</param>
    /// <param name="metadata">File metadata for additional detection</param>
    /// <returns>True if the file is an XML file or contains standardized XML content</returns>
    public static bool IsXmlFile(string fileExtension, string mimeType, JsonElement metadata)
    {
        // Check for XML file extensions and MIME types
        if (IsXmlFile(fileExtension, mimeType))
            return true;

        // Check if this file has standardized XML content (from StandardizerPlugin)
        return metadata.TryGetProperty("standardizedMetadataXml", out _);
    }

    /// <summary>
    /// Check if file is an image file based on extension and MIME type
    /// </summary>
    /// <param name="fileExtension">File extension (e.g., ".jpg")</param>
    /// <param name="mimeType">MIME type (e.g., "image/jpeg")</param>
    /// <returns>True if the file is an image file</returns>
    public static bool IsImageFile(string fileExtension, string mimeType) =>
        ImageExtensions.Contains(fileExtension?.ToLowerInvariant()) || 
        ImageMimeTypes.Any(mime => mimeType?.ToLowerInvariant().Contains(mime) == true);

    /// <summary>
    /// Check if file is a video file based on extension and MIME type
    /// </summary>
    /// <param name="fileExtension">File extension (e.g., ".mp4")</param>
    /// <param name="mimeType">MIME type (e.g., "video/mp4")</param>
    /// <returns>True if the file is a video file</returns>
    public static bool IsVideoFile(string fileExtension, string mimeType) =>
        VideoExtensions.Contains(fileExtension?.ToLowerInvariant()) ||
        VideoMimeTypes.Any(mime => mimeType?.ToLowerInvariant().Contains(mime) == true);

    /// <summary>
    /// Check if file is an information content file (text or other content types that can contain information about audio)
    /// </summary>
    /// <param name="fileExtension">File extension (e.g., ".info", ".meta")</param>
    /// <param name="mimeType">MIME type (e.g., "text/plain")</param>
    /// <returns>True if the file is an information content file</returns>
    public static bool IsInformationContentFile(string fileExtension, string mimeType)
    {
        // Information content files include text files and other content types
        var informationExtensions = new[] { ".info", ".meta", ".metadata", ".desc", ".description", ".notes", ".readme" };
        var informationMimeTypes = new[] { "text/plain", "text/markdown", "application/json" };

        return informationExtensions.Contains(fileExtension?.ToLowerInvariant()) ||
               informationMimeTypes.Any(mime => mimeType?.ToLowerInvariant().Contains(mime) == true);
    }

    /// <summary>
    /// Get the general file type category
    /// </summary>
    /// <param name="fileExtension">File extension</param>
    /// <param name="mimeType">MIME type</param>
    /// <returns>File type category as string</returns>
    public static string GetFileTypeCategory(string fileExtension, string mimeType)
    {
        if (IsAudioFile(fileExtension, mimeType)) return "Audio";
        if (IsVideoFile(fileExtension, mimeType)) return "Video";
        if (IsImageFile(fileExtension, mimeType)) return "Image";
        if (IsXmlFile(fileExtension, mimeType)) return "XML";
        if (IsTextFile(fileExtension, mimeType)) return "Text";
        return "Unknown";
    }

    /// <summary>
    /// Get all supported audio file extensions
    /// </summary>
    /// <returns>Array of audio file extensions</returns>
    public static string[] GetSupportedAudioExtensions() => AudioExtensions.ToArray();

    /// <summary>
    /// Get all supported text file extensions
    /// </summary>
    /// <returns>Array of text file extensions</returns>
    public static string[] GetSupportedTextExtensions() => TextExtensions.ToArray();

    /// <summary>
    /// Get all supported XML file extensions
    /// </summary>
    /// <returns>Array of XML file extensions</returns>
    public static string[] GetSupportedXmlExtensions() => XmlExtensions.ToArray();

    /// <summary>
    /// Get all supported image file extensions
    /// </summary>
    /// <returns>Array of image file extensions</returns>
    public static string[] GetSupportedImageExtensions() => ImageExtensions.ToArray();

    /// <summary>
    /// Get all supported video file extensions
    /// </summary>
    /// <returns>Array of video file extensions</returns>
    public static string[] GetSupportedVideoExtensions() => VideoExtensions.ToArray();
}
