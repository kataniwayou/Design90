using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Shared.Correlation;

namespace Plugin.Shared.Utilities;

/// <summary>
/// Audio file information extracted from file headers
/// </summary>
public class AudioFileInfo
{
    /// <summary>
    /// Number of audio channels (1 = mono, 2 = stereo, etc.)
    /// </summary>
    public int Channels { get; set; }

    /// <summary>
    /// Duration of the audio file in seconds
    /// </summary>
    public double DurationSeconds { get; set; }

    /// <summary>
    /// Sample rate in Hz
    /// </summary>
    public int SampleRate { get; set; }

    /// <summary>
    /// Bits per sample
    /// </summary>
    public int BitsPerSample { get; set; }

    /// <summary>
    /// Audio format type
    /// </summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>
    /// Whether the analysis was successful
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Error message if analysis failed
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Utility for analyzing audio file headers to extract channel count, duration, and other metadata
/// Supports WAV, AMR, AWB, and FLAC formats
/// </summary>
public static class AudioFileAnalyzer
{
    /// <summary>
    /// Analyzes audio file content and extracts header information including channels and duration
    /// </summary>
    /// <param name="fileContent">Audio file content as byte array</param>
    /// <param name="fileName">Original file name (used to determine format)</param>
    /// <param name="logger">Logger for correlation-aware logging</param>
    /// <returns>Audio file information including channels, duration, and format details</returns>
    public static AudioFileInfo AnalyzeAudioFile(byte[] fileContent, string fileName, ILogger? logger = null)
    {
        try
        {
            if (fileContent == null || fileContent.Length == 0)
            {
                var error = "Audio file content is null or empty";
                logger?.LogWarningWithCorrelation(error);
                return new AudioFileInfo { IsValid = false, ErrorMessage = error };
            }

            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            logger?.LogDebugWithCorrelation("Analyzing audio file content: {FileName}, Extension: {Extension}, Size: {Size} bytes",
                fileName, extension, fileContent.Length);

            return extension switch
            {
                ".wav" => AnalyzeWavContent(fileContent, logger),
                ".flac" => AnalyzeFlacContent(fileContent, logger),
                ".amr" or ".awb" => AnalyzeAmrContent(fileContent, logger),
                _ => new AudioFileInfo
                {
                    IsValid = false,
                    ErrorMessage = $"Unsupported audio format: {extension}",
                    Format = extension
                }
            };
        }
        catch (Exception ex)
        {
            var error = $"Failed to analyze audio file content: {ex.Message}";
            logger?.LogErrorWithCorrelation(ex, "Audio file analysis failed for: {FileName}", fileName);
            return new AudioFileInfo { IsValid = false, ErrorMessage = error };
        }
    }

    /// <summary>
    /// Analyzes WAV file content using NAudio
    /// </summary>
    private static AudioFileInfo AnalyzeWavContent(byte[] fileContent, ILogger? logger)
    {
        try
        {
            using var memoryStream = new MemoryStream(fileContent);
            using var reader = new WaveFileReader(memoryStream);
            var format = reader.WaveFormat;
            var duration = reader.TotalTime.TotalSeconds;

            logger?.LogDebugWithCorrelation("WAV content analyzed: Channels={Channels}, Duration={Duration}s, SampleRate={SampleRate}",
                format.Channels, duration, format.SampleRate);

            return new AudioFileInfo
            {
                Channels = format.Channels,
                DurationSeconds = duration,
                SampleRate = format.SampleRate,
                BitsPerSample = format.BitsPerSample,
                Format = "WAV",
                IsValid = true
            };
        }
        catch (Exception ex)
        {
            var error = $"Failed to analyze WAV content: {ex.Message}";
            logger?.LogErrorWithCorrelation(ex, "WAV content analysis failed");
            return new AudioFileInfo { IsValid = false, ErrorMessage = error, Format = "WAV" };
        }
    }

    /// <summary>
    /// Analyzes FLAC file content by reading header information manually
    /// FLAC files have a specific header format that can be parsed
    /// </summary>
    private static AudioFileInfo AnalyzeFlacContent(byte[] fileContent, ILogger? logger)
    {
        try
        {
            using var memoryStream = new MemoryStream(fileContent);
            using var reader = new BinaryReader(memoryStream);

            // Read FLAC signature
            var signature = reader.ReadBytes(4);
            if (signature.Length < 4 || System.Text.Encoding.ASCII.GetString(signature) != "fLaC")
            {
                return new AudioFileInfo { IsValid = false, ErrorMessage = "Invalid FLAC file: missing signature", Format = "FLAC" };
            }

            // Read metadata blocks to find STREAMINFO
            while (true)
            {
                var blockHeader = reader.ReadByte();
                var isLastBlock = (blockHeader & 0x80) != 0;
                var blockType = blockHeader & 0x7F;

                var blockSize = (reader.ReadByte() << 16) | (reader.ReadByte() << 8) | reader.ReadByte();

                if (blockType == 0) // STREAMINFO block
                {
                    // Skip minimum/maximum block size (4 bytes)
                    reader.ReadBytes(4);

                    // Skip minimum/maximum frame size (6 bytes)
                    reader.ReadBytes(6);

                    // Read sample rate (20 bits), channels (3 bits), bits per sample (5 bits)
                    var sampleRateHigh = reader.ReadByte();
                    var sampleRateMidAndChannels = reader.ReadByte();
                    var channelsAndBitsPerSample = reader.ReadByte();

                    var sampleRate = (sampleRateHigh << 12) | (sampleRateMidAndChannels << 4) | ((channelsAndBitsPerSample & 0xF0) >> 4);
                    var channels = ((sampleRateMidAndChannels & 0x0E) >> 1) + 1;
                    var bitsPerSample = ((channelsAndBitsPerSample & 0x0F) << 1) | (reader.ReadByte() >> 7) + 1;

                    // Read total samples (36 bits)
                    var totalSamplesBytes = reader.ReadBytes(5);
                    var totalSamples = ((long)(totalSamplesBytes[0] & 0x0F) << 32) |
                                     ((long)totalSamplesBytes[1] << 24) |
                                     ((long)totalSamplesBytes[2] << 16) |
                                     ((long)totalSamplesBytes[3] << 8) |
                                     totalSamplesBytes[4];

                    var duration = totalSamples > 0 ? (double)totalSamples / sampleRate : 0;

                    logger?.LogDebugWithCorrelation("FLAC file analyzed: Channels={Channels}, Duration={Duration}s, SampleRate={SampleRate}",
                        channels, duration, sampleRate);

                    return new AudioFileInfo
                    {
                        Channels = channels,
                        DurationSeconds = duration,
                        SampleRate = sampleRate,
                        BitsPerSample = bitsPerSample,
                        Format = "FLAC",
                        IsValid = true
                    };
                }
                else
                {
                    // Skip this metadata block
                    reader.ReadBytes(blockSize);
                }

                if (isLastBlock)
                    break;
            }

            return new AudioFileInfo { IsValid = false, ErrorMessage = "FLAC STREAMINFO block not found", Format = "FLAC" };
        }
        catch (Exception ex)
        {
            var error = $"Failed to analyze FLAC content: {ex.Message}";
            logger?.LogErrorWithCorrelation(ex, "FLAC content analysis failed");
            return new AudioFileInfo { IsValid = false, ErrorMessage = error, Format = "FLAC" };
        }
    }

    /// <summary>
    /// Analyzes AMR/AWB file content by reading header information manually
    /// AMR files have a specific header format that can be parsed
    /// </summary>
    private static AudioFileInfo AnalyzeAmrContent(byte[] fileContent, ILogger? logger)
    {
        try
        {
            using var memoryStream = new MemoryStream(fileContent);
            using var reader = new BinaryReader(memoryStream);

            // Read potential AMR header (up to 9 bytes for AMR-WB)
            var headerBytes = new byte[9];
            var bytesRead = reader.Read(headerBytes, 0, 9);

            if (bytesRead < 6)
            {
                return new AudioFileInfo { IsValid = false, ErrorMessage = "Invalid AMR file: insufficient header data", Format = "AMR" };
            }

            // Check for AMR magic numbers
            var headerString = System.Text.Encoding.ASCII.GetString(headerBytes, 0, bytesRead);
            bool isAmrNb = headerString.StartsWith("#!AMR\n");
            bool isAmrWb = headerString.StartsWith("#!AMR-WB\n");

            if (!isAmrNb && !isAmrWb)
            {
                return new AudioFileInfo { IsValid = false, ErrorMessage = "Invalid AMR file: missing magic number", Format = "AMR" };
            }

            // AMR-NB is always mono, 8kHz; AMR-WB is always mono, 16kHz
            var sampleRate = isAmrWb ? 16000 : 8000;
            var format = isAmrWb ? "AMR-WB" : "AMR-NB";
            var headerSize = isAmrWb ? 9 : 6;

            // Estimate duration by file size (rough approximation)
            // AMR-NB average bitrate ~12.2 kbps, AMR-WB ~23.85 kbps
            var dataSize = fileContent.Length - headerSize;
            var avgBitrate = isAmrWb ? 23850 : 12200; // bits per second
            var estimatedDuration = (dataSize * 8.0) / avgBitrate;

            logger?.LogDebugWithCorrelation("AMR file analyzed: Format={Format}, EstimatedDuration={Duration}s, SampleRate={SampleRate}",
                format, estimatedDuration, sampleRate);

            return new AudioFileInfo
            {
                Channels = 1, // AMR is always mono
                DurationSeconds = estimatedDuration,
                SampleRate = sampleRate,
                BitsPerSample = 16, // AMR uses 16-bit samples internally
                Format = format,
                IsValid = true
            };
        }
        catch (Exception ex)
        {
            var error = $"Failed to analyze AMR content: {ex.Message}";
            logger?.LogErrorWithCorrelation(ex, "AMR content analysis failed");
            return new AudioFileInfo { IsValid = false, ErrorMessage = error, Format = "AMR" };
        }
    }
}
