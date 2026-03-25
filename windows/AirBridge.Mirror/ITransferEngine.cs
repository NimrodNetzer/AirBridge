using AirBridge.Core.Interfaces;

namespace AirBridge.Mirror;

/// <summary>
/// Abstraction over the file-transfer engine used by the mirror module.
/// Allows the mirror session to initiate a send without depending directly
/// on the concrete <see cref="AirBridge.Transfer.TransferSession"/> type.
/// </summary>
public interface ITransferEngine
{
    /// <summary>
    /// Sends a file at <paramref name="filePath"/> over <paramref name="networkStream"/>.
    /// Progress updates (bytes transferred so far) are emitted via <paramref name="progress"/>.
    /// </summary>
    /// <param name="filePath">Absolute path to the local file to send.</param>
    /// <param name="networkStream">
    ///   Writable stream connected to the remote receiver.
    ///   The implementation writes length-prefixed transfer protocol messages.
    /// </param>
    /// <param name="progress">Optional progress callback; receives bytes-transferred counts.</param>
    /// <param name="cancellationToken">Token that cancels the transfer in progress.</param>
    /// <returns>
    ///   A <see cref="TransferEngineResult"/> indicating success or failure.
    ///   Exceptions from the underlying I/O are caught and returned as
    ///   <see cref="TransferEngineResult.Failure"/>.
    /// </returns>
    Task<TransferEngineResult> SendFileAsync(
        string filePath,
        Stream networkStream,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result returned by <see cref="ITransferEngine.SendFileAsync"/>.
/// </summary>
public sealed record TransferEngineResult(bool IsSuccess, string? ErrorMessage = null)
{
    /// <summary>Singleton success value.</summary>
    public static readonly TransferEngineResult Success = new(true);

    /// <summary>Creates a failure result with the given message.</summary>
    public static TransferEngineResult Failure(string message) => new(false, message);
}
