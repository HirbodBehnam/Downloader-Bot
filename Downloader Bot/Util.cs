using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Linq;

namespace DownloaderBot
{
	internal static class Util
	{
		/// <summary>
		/// Converts byte to human readable amount https://stackoverflow.com/a/4975942/4213397
		/// </summary>
		/// <param name="byteCount"></param>
		/// <returns></returns>
		public static string BytesToString(long byteCount)
		{
			string[] suf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" }; //Longs run out around EB
			if (byteCount == 0)
				return "0" + suf[0];
			long bytes = Math.Abs(byteCount);
			int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
			double num = Math.Round(bytes / Math.Pow(1024, place), 1);
			return (Math.Sign(byteCount) * num).ToString(CultureInfo.InvariantCulture) + suf[place];
		}

		/// <summary>
		/// Gets the size of remote file without downloading it. https://stackoverflow.com/a/12079865/4213397
		/// </summary>
		/// <param name="url">The URL to check</param>
		/// <returns>The size in bytes; -1 if fails</returns>
		public static async Task<long> GetSizeOfFile(string url)
		{
			long res = -1;
			using HttpClient client = new();
			try
			{
				var result = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
				res = result.Content.Headers.ContentLength ?? -1;
			}
			catch (Exception)
			{
				// ignored
			}

			return res;
		}

		/// <summary>
		/// Checks whether the url is valid or not
		/// </summary>
		/// <param name="url">The url to check</param>
		/// <returns>True if valid</returns>
		public static bool ValidateUrl(string url)
		{
			return Uri.TryCreate(url, UriKind.Absolute, out var uriResult)
				   && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
		}

		/// <summary>
		/// Logs a message to terminal with time
		/// </summary>
		/// <param name="m">Message</param>
		public static void Log(object m)
		{
			Console.WriteLine($"[{DateTime.Now:G}]: {m}");
		}

		/// <summary>
		/// Get file name from URL
		/// </summary>
		/// <param name="url"></param>
		/// <returns></returns>
		public static string GetFileNameFromUrl(string url)
		{
			string[] parts1 = url.Split('/');
			string[] parts2 = parts1[^1].Split('?');
			return parts2[0];
		}

		/// <summary>
		/// Checks if the extenstion of the file is supported by telegram for direct upload
		/// </summary>
		/// <param name="name">The file name</param>
		/// <returns></returns>
		public static bool CheckExtenstion(string name)
		{
			string[] extensions = { ".zip", ".pdf", ".gif", ".mp3", ".ogg", ".jpg", ".png", ".mp4" };
			return extensions.Contains(Path.GetExtension(name));
		}
	}
}
