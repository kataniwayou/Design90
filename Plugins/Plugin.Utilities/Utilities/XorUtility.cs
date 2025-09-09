using Microsoft.Extensions.Logging;
using Shared.Correlation;

namespace Plugin.Shared.Utilities;

/// <summary>
/// Utility for XOR operations on byte data with configurable keys
/// Provides methods for encrypting/decrypting data using XOR cipher
/// </summary>
public static class XorUtility
{
    /// <summary>
    /// Performs XOR operation on data using a single byte key
    /// </summary>
    /// <param name="data">Data to XOR</param>
    /// <param name="key">Single byte XOR key</param>
    /// <param name="logger">Optional logger for correlation-aware logging</param>
    /// <returns>XOR result as byte array</returns>
    public static byte[] XorWithByte(byte[] data, byte key, ILogger? logger = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        logger?.LogDebugWithCorrelation("Performing XOR operation with single byte key on {DataLength} bytes", data.Length);

        var result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            result[i] = (byte)(data[i] ^ key);
        }

        logger?.LogDebugWithCorrelation("XOR operation completed successfully");
        return result;
    }

    /// <summary>
    /// Performs XOR operation on data using a byte array key (repeating pattern)
    /// </summary>
    /// <param name="data">Data to XOR</param>
    /// <param name="key">Byte array XOR key (will repeat if shorter than data)</param>
    /// <param name="logger">Optional logger for correlation-aware logging</param>
    /// <returns>XOR result as byte array</returns>
    public static byte[] XorWithByteArray(byte[] data, byte[] key, ILogger? logger = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (key == null || key.Length == 0)
        {
            throw new ArgumentException("XOR key cannot be null or empty", nameof(key));
        }

        logger?.LogDebugWithCorrelation("Performing XOR operation with {KeyLength}-byte key on {DataLength} bytes", 
            key.Length, data.Length);

        var result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            result[i] = (byte)(data[i] ^ key[i % key.Length]);
        }

        logger?.LogDebugWithCorrelation("XOR operation completed successfully");
        return result;
    }

    /// <summary>
    /// Performs XOR operation on data using a string key (converted to UTF-8 bytes)
    /// </summary>
    /// <param name="data">Data to XOR</param>
    /// <param name="key">String XOR key (will be converted to UTF-8 bytes)</param>
    /// <param name="logger">Optional logger for correlation-aware logging</param>
    /// <returns>XOR result as byte array</returns>
    public static byte[] XorWithString(byte[] data, string key, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("XOR key cannot be null or empty", nameof(key));
        }

        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        logger?.LogDebugWithCorrelation("Converting string key '{Key}' to {KeyLength} UTF-8 bytes", key, keyBytes.Length);

        return XorWithByteArray(data, keyBytes, logger);
    }



    /// <summary>
    /// Performs in-place XOR operation on data using a single byte key
    /// Modifies the original data array
    /// </summary>
    /// <param name="data">Data to XOR (modified in place)</param>
    /// <param name="key">Single byte XOR key</param>
    /// <param name="logger">Optional logger for correlation-aware logging</param>
    public static void XorInPlace(byte[] data, byte key, ILogger? logger = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        logger?.LogDebugWithCorrelation("Performing in-place XOR operation with single byte key on {DataLength} bytes", data.Length);

        for (int i = 0; i < data.Length; i++)
        {
            data[i] ^= key;
        }

        logger?.LogDebugWithCorrelation("In-place XOR operation completed successfully");
    }

    /// <summary>
    /// Performs in-place XOR operation on data using a byte array key
    /// Modifies the original data array
    /// </summary>
    /// <param name="data">Data to XOR (modified in place)</param>
    /// <param name="key">Byte array XOR key</param>
    /// <param name="logger">Optional logger for correlation-aware logging</param>
    public static void XorInPlace(byte[] data, byte[] key, ILogger? logger = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (key == null || key.Length == 0)
        {
            throw new ArgumentException("XOR key cannot be null or empty", nameof(key));
        }

        logger?.LogDebugWithCorrelation("Performing in-place XOR operation with {KeyLength}-byte key on {DataLength} bytes", 
            key.Length, data.Length);

        for (int i = 0; i < data.Length; i++)
        {
            data[i] ^= key[i % key.Length];
        }

        logger?.LogDebugWithCorrelation("In-place XOR operation completed successfully");
    }

    /// <summary>
    /// Calculates a simple XOR checksum of the data
    /// </summary>
    /// <param name="data">Data to calculate checksum for</param>
    /// <param name="logger">Optional logger for correlation-aware logging</param>
    /// <returns>XOR checksum as a single byte</returns>
    public static byte CalculateXorChecksum(byte[] data, ILogger? logger = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        logger?.LogDebugWithCorrelation("Calculating XOR checksum for {DataLength} bytes", data.Length);

        byte checksum = 0;
        foreach (byte b in data)
        {
            checksum ^= b;
        }

        logger?.LogDebugWithCorrelation("XOR checksum calculated: 0x{Checksum:X2}", checksum);
        return checksum;
    }
}
