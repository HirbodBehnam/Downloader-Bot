using Newtonsoft.Json;
using System;
using System.IO;
using System.Net;
using System.Threading;
using Ionic.Zip;
using Telegram.Bot;
using Telegram.Bot.Types.InputFiles;

namespace Downloader_Bot
{
    //dotnet add package DotNetZip --version 1.13.4
    //dotnet add package Telegram.Bot --version 15.0.0
    class ConfigStruct
    {
        public string Token;
        public string DownloadPath;
        public int[] Admins;
        public long MaxFileSize;
    }

    class Program
    {
        private const int MaxFileSize = 1024 * 1024 * 47;
        private static ConfigStruct _config;
        private static TelegramBotClient _bot;
        private static string _downloadPath;
        private static bool _freeBot;

        static void Main(string[] args)
        {
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
                        break;
                    case "/id":
                        await _bot.SendTextMessageAsync(e.Message.Chat, e.Message.Chat.Id.ToString());
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

                            //Then check the file size; If it is less than 20MB use telegram itself
                            long size = GetSizeOfFile(e.Message.Text);
                            if (size < 1) //Either an error or file size is really 0
                            {
                                await _bot.SendTextMessageAsync(e.Message.Chat,
                                    "Error on getting file size or the file size is 0");
                            }
                            else if (size < _config.MaxFileSize) //Download the file, zip it and send it
                            {
                                var msg = await _bot.SendTextMessageAsync(e.Message.Chat,
                                    "Downloading the file on server");
                                string dir = new Random().Next().ToString();
                                var d = Directory.CreateDirectory(Path.Combine(_downloadPath, dir));
                                try
                                {
                                    //At first download the whole file
                                    using (WebClient wc = new WebClient())
                                    {
                                        wc.DownloadFile(e.Message.Text,
                                            Path.Combine(_downloadPath, dir, GetFileNameFromUrl(e.Message.Text)));
                                    }
                                }
                                catch (Exception)
                                {
                                    Log("Error downloading " + e.Message.Text);
                                    await _bot.SendTextMessageAsync(e.Message.Chat,
                                        "Error downloading " + e.Message.Text);
                                    await _bot.DeleteMessageAsync(e.Message.Chat, msg.MessageId);
                                    return;
                                }

                                if (size < _config.MaxFileSize)//Send the file directly
                                {
                                    using (FileStream fs = File.OpenRead(_downloadPath))
                                    {
                                        InputOnlineFile inputOnlineFile =
                                            new InputOnlineFile(fs, Path.GetFileName(_downloadPath));
                                        await _bot.SendDocumentAsync(e.Message.Chat, inputOnlineFile);
                                    }
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
                                            await _bot.SendDocumentAsync(e.Message.Chat, inputOnlineFile);
                                        }
                                    }

                                }
                                await _bot.DeleteMessageAsync(e.Message.Chat, msg.MessageId);
                                d.Delete(true);
                            }
                            else
                            {
                                await _bot.SendTextMessageAsync(e.Message.Chat,
                                    "File is too large for bot! (" + size + ")");
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
        private static long GetSizeOfFile(string url)
        {
            try
            {
                using (WebClient wc = new WebClient())
                {
                    wc.OpenRead(url);
                    return Convert.ToInt64(wc.ResponseHeaders["Content-Length"]);
                }
            }
            catch (Exception)
            {
                // ignored
            }

            return -1;
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
    }
}