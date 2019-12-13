using Newtonsoft.Json;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Ionic.Zip;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InputFiles;

namespace Downloader_Bot
{
    //dotnet add package DotNetZip --version 1.13.4
    //dotnet add package Telegram.Bot --version 15.0.0
    internal class ConfigStruct
    {
        public string Token;
        public string DownloadPath;
        public int[] Admins;
        public long MaxFileSize;
    }

    class Program
    {
        private const int MaxFileSize = 1000 * 1000 * 50,MaxTelegramSize = 20 * 1000 * 1000; //For some reasons, looks like there is some problems with 1024 * 1024 * 50 
        private const string Version = "1.0.2";
        private static ConfigStruct _config;
        private static TelegramBotClient _bot;
        private static string _downloadPath;
        private static bool _freeBot;

        static void Main(string[] args)
        {
            Console.WriteLine("Downloader Bot Version " + Version);
            //Load the config file
            try
            {
                string configText = File.ReadAllText(args.Length == 0 ? "config.json" : args[0]);
                _config = JsonConvert.DeserializeObject<ConfigStruct>(configText);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                Environment.Exit(1);
            }

            _freeBot = _config.Admins == null || _config.Admins.Length == 0;
            //Create download folder
            _downloadPath = string.IsNullOrWhiteSpace(_config.DownloadPath)
                ? Environment.CurrentDirectory
                : _config.DownloadPath;
            Directory.CreateDirectory(_downloadPath);
            //Setup the bot
            _bot = new TelegramBotClient(_config.Token);
            Log("Authorized on bot " + _bot.GetMeAsync().Result.Username);
            _bot.OnMessage += Bot_OnMessage;
            _bot.StartReceiving();
            while (true)
                Thread.Sleep(int.MaxValue);
        }

        private static async void Bot_OnMessage(object sender, Telegram.Bot.Args.MessageEventArgs e)
        {
            if (e.Message.Text != null)
            {
                switch (e.Message.Text)
                {
                    case "/start":
                        await _bot.SendTextMessageAsync(e.Message.Chat, "Welcome!\nJust send the link to the bot.");
                        break;
                    case "/id":
                        await _bot.SendTextMessageAsync(e.Message.Chat, e.Message.From.Id.ToString());
                        break;
                    default:
                        if (!_freeBot && !Array.Exists(_config.Admins, id => id == e.Message.From.Id))
                            Log("Unauthorized access from user " + e.Message.From.FirstName + " " +
                                e.Message.From.LastName + "; ID: " + e.Message.From.Id);
                        else
                        {
                            //At first check if the url is valid
                            if (!ValidateUrl(e.Message.Text))
                            {
                                await _bot.SendTextMessageAsync(e.Message.Chat, "The URL is not valid.");
                                return;
                            }
                            var msg = await _bot.SendTextMessageAsync(e.Message.Chat, "Getting some info about file...",ParseMode.Default,true,false,e.Message.MessageId);
                            //Then check the file size; If it is less than 20MB use telegram itself
                            long size = await GetSizeOfFile(e.Message.Text);
                            if (size < 1) //Either an error or file size is really 0
                            {
                                await _bot.EditMessageTextAsync(e.Message.Chat,msg.MessageId,
                                    "Error on getting file size or the file size is 0");
                                return;
                            }
                            if (size < MaxTelegramSize && CheckExtenstion(GetFileNameFromUrl(e.Message.Text)))
                            {
                                try
                                {
                                    InputOnlineFile inputOnlineFile = new InputOnlineFile(e.Message.Text);
                                    await _bot.SendDocumentAsync(e.Message.Chat, inputOnlineFile,null,ParseMode.Default,
                                        false,e.Message.MessageId);
                                    await _bot.DeleteMessageAsync(e.Message.Chat, msg.MessageId);
                                    break; //If the file is uploaded, do not continue to download it
                                }
                                catch (Exception ex)
                                {
                                    Log("Error: Cannot download directly from Telegram:" + ex.Message);
                                }
                            }
                            if (size < _config.MaxFileSize) //Download the file, zip it and send it
                            {
                                string dir = new Random().Next().ToString();
                                var d = Directory.CreateDirectory(Path.Combine(_downloadPath, dir));
                                WebClient wc = new WebClient();
                                try
                                {
                                    int percent = 0;
                                    long toDownload = 1, downloaded = 0, lastTimeDownloaded = 0;
                                    string lastMsg = ""; //Do not edit the message and send exactly the same thing
                                    //At first download the whole file
                                    wc.DownloadProgressChanged += (o, args) =>
                                    {
                                        percent = args.ProgressPercentage;
                                        toDownload = args.TotalBytesToReceive;
                                        downloaded = args.BytesReceived;
                                    };
                                    wc.DownloadFileAsync(new Uri(e.Message.Text),
                                        Path.Combine(_downloadPath, dir, GetFileNameFromUrl(e.Message.Text)));
                                    while (wc.IsBusy)
                                    {
                                        string m = percent + "% Completed.\n" + BytesToString(downloaded) + " from " +
                                                   BytesToString(toDownload) + "  " +
                                                   BytesToString(downloaded - lastTimeDownloaded) +
                                                   "/s"; //Do not edit the message and send exactly the same thing
                                        if (m != lastMsg)
                                        {
                                            lastMsg = m;
                                            m += "\n[";
                                            for (int i = 0; i < percent / 10; i++)
                                                m += "#";
                                            for (int i = 0; i < 10 - percent / 10; i++)
                                                m += "⠀"; //This is not space
                                            m += "]";
                                            await _bot.EditMessageTextAsync(e.Message.Chat, msg.MessageId, "Downloading file on server:\n" + m);
                                        }

                                        lastTimeDownloaded = downloaded;
                                        await Task.Delay(1000);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log("Error downloading " + e.Message.Text + ": " + ex.Message);
                                    await _bot.EditMessageTextAsync(e.Message.Chat,msg.MessageId,
                                        "Error downloading " + e.Message.Text);
                                    return;
                                }
                                finally
                                {
                                    wc.Dispose();
                                }

                                if (size < MaxTelegramSize) //Send the file directly
                                {
                                    using (FileStream fs = File.OpenRead(Path.Combine(_downloadPath, dir, GetFileNameFromUrl(e.Message.Text))))
                                    {
                                        InputOnlineFile inputOnlineFile =
                                            new InputOnlineFile(fs, Path.GetFileName(Path.Combine(_downloadPath, dir, GetFileNameFromUrl(e.Message.Text))));
                                        await _bot.SendDocumentAsync(e.Message.Chat, inputOnlineFile,null,ParseMode.Default,
                                            false,e.Message.MessageId);
                                    }
                                    await _bot.DeleteMessageAsync(e.Message.Chat, msg.MessageId);
                                }
                                else
                                {
                                    //Then zip the file
                                    await _bot.EditMessageTextAsync(e.Message.Chat, msg.MessageId, "Zipping file");
                                    using (ZipFile zip = new ZipFile())
                                    {
                                        zip.AddFile(
                                            Path.Combine(_downloadPath, dir, GetFileNameFromUrl(e.Message.Text)),
                                            "");
                                        zip.MaxOutputSegmentSize = MaxFileSize;
                                        zip.Save(Path.Combine(_downloadPath, dir, GetFileNameFromUrl(e.Message.Text)) +
                                                 ".zip");
                                    }

                                    File.Delete(Path.Combine(_downloadPath, dir, GetFileNameFromUrl(e.Message.Text)));
                                    //Send all of the zip files to telegram
                                    var files = Directory.GetFiles(Path.Combine(_downloadPath, dir));
                                    for (int i = 0; i < files.Length; i++)
                                    {
                                        await _bot.EditMessageTextAsync(e.Message.Chat, msg.MessageId,
                                            $"Uploading file {i + 1}/{files.Length}");
                                        using (FileStream fs = File.OpenRead(files[i]))
                                        {
                                            InputOnlineFile inputOnlineFile =
                                                new InputOnlineFile(fs, Path.GetFileName(files[i]));
                                            await _bot.SendDocumentAsync(e.Message.Chat, inputOnlineFile,
                                                null,ParseMode.Default,false,e.Message.MessageId);
                                        }
                                    }

                                }
                                await _bot.DeleteMessageAsync(e.Message.Chat, msg.MessageId);
                                d.Delete(true);
                            }
                            else
                            {
                                await _bot.EditMessageTextAsync(e.Message.Chat,msg.MessageId,
                                    "File is too large for bot! (file size is " + size + " bytes)");
                            }
                        }

                        break;
                }
            }
        }

        /// <summary>
        /// Gets the size of remote file without downloading it. https://stackoverflow.com/a/12079865/4213397
        /// </summary>
        /// <param name="url">The URL to check</param>
        /// <returns>The size in bytes; -1 if fails</returns>
        private static async Task<long> GetSizeOfFile(string url)
        {
            long res = -1;
            WebClient wc = new WebClient();
            try
            {
                await wc.OpenReadTaskAsync(url);
                res = Convert.ToInt64(wc.ResponseHeaders["Content-Length"]);
            }
            catch (Exception)
            {
                // ignored
            }
            finally
            {
                wc.Dispose();
            }

            return res;
        }

        /// <summary>
        /// Checks whether the url is valid or not
        /// </summary>
        /// <param name="url">The url to check</param>
        /// <returns>True if valid</returns>
        private static bool ValidateUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uriResult)
                   && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
        }

        /// <summary>
        /// Logs a message to terminal with time
        /// </summary>
        /// <param name="m">Message</param>
        private static void Log(object m)
        {
            Console.WriteLine($"[{DateTime.Now:G}]: {m}");
        }

        /// <summary>
        /// Get file name from URL
        /// </summary>
        /// <param name="url"></param>
        /// <returns></returns>
        private static string GetFileNameFromUrl(string url)
        {
            string[] parts1 = url.Split('/');
            string[] parts2 = parts1[parts1.Length - 1].Split('?');
            return parts2[0];
        }
        /// <summary>
        /// Checks if the extenstion of the file is supported by telegram for direct upload
        /// </summary>
        /// <param name="name">The file name</param>
        /// <returns></returns>
        private static bool CheckExtenstion(string name)
        {
            string[] extensions = {".zip",".pdf",".gif",".mp3",".ogg",".jpg",".png",".mp4"};
            return extensions.Contains(Path.GetExtension(name));
        }
        /// <summary>
        /// Converts byte to human readable amount https://stackoverflow.com/a/4975942/4213397
        /// </summary>
        /// <param name="byteCount"></param>
        /// <returns></returns>
        private static string BytesToString(long byteCount)
        {
            string[] suf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" }; //Longs run out around EB
            if (byteCount == 0)
                return "0" + suf[0];
            long bytes = Math.Abs(byteCount);
            int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
            double num = Math.Round(bytes / Math.Pow(1024, place), 1);
            return (Math.Sign(byteCount) * num).ToString(CultureInfo.InvariantCulture) + suf[place];
        }
    }
}