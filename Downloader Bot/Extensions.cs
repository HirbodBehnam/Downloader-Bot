using DownloaderBot;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

internal static class Extensions
{
    // From https://stackoverflow.com/a/46497896/4213397
    public static async Task DownloadAsync(this HttpClient client, string requestUri, Stream destination, IProgress<ProgressData> progress = null, CancellationToken cancellationToken = default)
    {
		// Get the http headers first to examine the content length
		using var response = await client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		var contentLength = response.Content.Headers.ContentLength;
		using var download = await response.Content.ReadAsStreamAsync(cancellationToken);

		// Ignore progress reporting when no progress reporter was 
		// passed or when the content length is unknown
		if (progress == null || !contentLength.HasValue)
		{
			await download.CopyToAsync(destination, cancellationToken);
			return;
		}

		// Use extension method to report progress while downloading
		await download.CopyToAsync(destination, 81920, contentLength.Value, progress, cancellationToken);
		progress.Report(new ProgressData(contentLength.Value, contentLength.Value));
	}

    public static async Task CopyToAsync(this Stream source, Stream destination, int bufferSize, long totalSize, IProgress<ProgressData> progress = null, CancellationToken cancellationToken = default)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        if (!source.CanRead)
            throw new ArgumentException("Has to be readable", nameof(source));
        if (destination == null)
            throw new ArgumentNullException(nameof(destination));
        if (!destination.CanWrite)
            throw new ArgumentException("Has to be writable", nameof(destination));
        if (bufferSize < 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));

        var buffer = new byte[bufferSize];
        long totalBytesRead = 0;
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            totalBytesRead += bytesRead;
            progress?.Report(new ProgressData(totalSize ,totalBytesRead));
        }
    }
}
