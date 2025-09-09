using Plugin.AudioConverter.Models;

namespace Plugin.AudioConverter.Services;

/// <summary>
/// Interface for FFmpeg operations
/// </summary>
public interface IFFmpegService
{
    /// <summary>
    /// Convert audio data using FFmpeg
    /// </summary>
    /// <param name="audioData">Input audio data as byte array</param>
    /// <param name="ffmpegArguments">FFmpeg conversion arguments</param>
    /// <param name="ffmpegPath">Custom path to FFmpeg executable, or null to use system PATH</param>
    /// <returns>FFmpeg execution result</returns>
    Task<FFmpegExecutionResult> ConvertAudioAsync(byte[] audioData, string ffmpegArguments, string? ffmpegPath = null);

    /// <summary>
    /// Check if FFmpeg is available and accessible
    /// </summary>
    /// <param name="ffmpegPath">Custom path to FFmpeg executable, or null to use system PATH</param>
    /// <returns>True if FFmpeg is available</returns>
    bool IsFFmpegAvailable(string? ffmpegPath = null);

    /// <summary>
    /// Build FFmpeg command string for byte array processing
    /// </summary>
    /// <param name="arguments">Conversion arguments</param>
    /// <returns>Complete FFmpeg command</returns>
    string BuildConversionCommand(string arguments);
}
