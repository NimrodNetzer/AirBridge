using AirBridge.Transfer;

namespace AirBridge.Mirror;

/// <summary>
/// Default <see cref="ITransferEngine"/> implementation that delegates to
/// <see cref="TransferSession"/> from the <c>AirBridge.Transfer</c> module.
/// </summary>
public sealed class MirrorTransferEngine : ITransferEngine
{
    /// <inheritdoc/>
    public async Task<TransferEngineResult> SendFileAsync(
        string filePath,
        Stream networkStream,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(networkStream);

        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
                return TransferEngineResult.Failure($"File not found: {filePath}");

            var sessionId  = Guid.NewGuid().ToString();
            var fileName   = fileInfo.Name;
            var totalBytes = fileInfo.Length;

            await using var fileStream = fileInfo.OpenRead();

            var session = new TransferSession(
                sessionId:     sessionId,
                fileName:      fileName,
                totalBytes:    totalBytes,
                isSender:      true,
                dataStream:    fileStream,
                networkStream: networkStream,
                progress:      progress);

            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            return TransferEngineResult.Success;
        }
        catch (OperationCanceledException)
        {
            return TransferEngineResult.Failure("Transfer was cancelled.");
        }
        catch (Exception ex)
        {
            return TransferEngineResult.Failure(ex.Message);
        }
    }
}
