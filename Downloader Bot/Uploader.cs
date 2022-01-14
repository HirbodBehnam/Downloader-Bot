using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace DownloaderBot
{
	// From https://stackoverflow.com/a/41392145/4213397
	internal class ProgressableStreamContent : HttpContent
	{
		private const int defaultBufferSize = 32 * 1024;

		private readonly HttpContent content;

		private readonly int bufferSize;

		private readonly IProgress<ProgressData> progress;

		public ProgressableStreamContent(HttpContent content, IProgress<ProgressData> progress) : this(content,
			defaultBufferSize, progress)
		{
		}

		public ProgressableStreamContent(HttpContent content, int bufferSize, IProgress<ProgressData> progress)
		{
			if (bufferSize <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(bufferSize));
			}

			this.content = content ?? throw new ArgumentNullException(nameof(content));
			this.bufferSize = bufferSize;
			this.progress = progress;

			foreach (var h in content.Headers)
			{
				Headers.Add(h.Key, h.Value);
			}
		}

		protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
		{
			var buffer = new byte[bufferSize];
			TryComputeLength(out long size);
			var uploaded = 0;


			using var sinput = await content.ReadAsStreamAsync();
			while (true)
			{
				var length = await sinput.ReadAsync(buffer);
				if (length <= 0)
					break;

				uploaded += length;
				progress?.Report(new ProgressData(size, uploaded));

				await stream.WriteAsync(buffer.AsMemory(0, length));
			}

			await stream.FlushAsync();
		}

		protected override bool TryComputeLength(out long length)
		{
			length = content.Headers.ContentLength.GetValueOrDefault();
			return true;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				content.Dispose();
			}

			base.Dispose(disposing);
		}
	}
}