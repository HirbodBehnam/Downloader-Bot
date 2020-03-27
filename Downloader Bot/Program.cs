using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ionic.Zip;
using Ionic.Zlib;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InputFiles;
using Telegram.Bot.Types.ReplyMarkups;

namespace Downloader_Bot
{
    internal class ConfigStruct
    {
        public string Token;
        public string DownloadPath;
        public int[] Admins;
        public long MaxFileSize;
    }

    class Program
    {
        private const int
            MaxFileSize = 1000 * 1000 * 50,
            MaxTelegramSize =
                20 * 1000 * 1000; //For some reasons, looks like there is some problems with 1024 * 1024 * 50 

        private const string Version = "1.1.0";
        private static ConfigStruct _config;
        private static TelegramBotClient _bot;
        private static string _downloadPath;
        private static bool _freeBot;
        private static ConcurrentDictionary<uint, bool> _downloadList; // True is downloading, false is canceled
        private static volatile uint _downloaderCounter;

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
            //Setup the cancel button list
            _downloadList = new ConcurrentDictionary<uint, bool>();
            //Setup the bot
            _bot = new TelegramBotClient(_config.Token);
            Log("Authorized on bot " + _bot.GetMeAsync().Result.Username);
            _bot.OnMessage += Bot_OnMessage;
            _bot.OnCallbackQuery += (sender, eventArgs) =>
                _downloadList[uint.Parse(eventArgs.CallbackQuery.Data)] = false;
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

                            var msg = await _bot.SendTextMessageAsync(e.Message.Chat, "Getting some info about file...",
                                ParseMode.Default, true, false, e.Message.MessageId);
                            //Then check the file size; If it is less than 20MB use telegram itself
                            long size = await GetSizeOfFile(e.Message.Text);
                            if (size < 1) //Either an error or file size is really 0
                            {
                                await _bot.EditMessageTextAsync(e.Message.Chat, msg.MessageId,
                                    "Error on getting file size or the file size is 0");
                                return;
                            }

                            if (size < MaxTelegramSize && CheckExtenstion(GetFileNameFromUrl(e.Message.Text)))
                            {
                                await _bot.EditMessageTextAsync(e.Message.Chat, msg.MessageId,
                                    "Telegram is directly downloading the file...");
                                try
                                {
                                    InputOnlineFile inputOnlineFile = new InputOnlineFile(e.Message.Text);
                                    await _bot.SendDocumentAsync(e.Message.Chat, inputOnlineFile, null,
                                        ParseMode.Default,
                                        false, e.Message.MessageId);
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
                                uint cancelTokenId = _downloaderCounter++;
                                //Create cancel button
                                var inlineKeyboardCancelBtn = new InlineKeyboardMarkup(new InlineKeyboardButton
                                {
                                    Text = "Cancel",
                                    CallbackData = cancelTokenId.ToString()
                                });
                                if (!_downloadList.TryAdd(cancelTokenId, true))
                                    inlineKeyboardCancelBtn = null;
                                //Now create directory and file for it
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
                                        if (_downloadList.TryGetValue(cancelTokenId, out bool downloading))
                                            if (!downloading)
                                            {
                                                wc.CancelAsync();
                                                throw new OperationCanceledException();
                                            }

                                        string m = percent + "% Completed\n" + BytesToString(downloaded) + " from " +
                                                   BytesToString(toDownload) + "  " +
                                                   BytesToString(downloaded - lastTimeDownloaded) +
                                                   "/s"; //Do not edit the message and send exactly the same thing
                                        if (m != lastMsg)
                                        {
                                            lastMsg = m;
                                            m += "\n[";
                                            for (int i = 0; i < percent / 10; i++)
                                                m += "█";
                                            for (int i = 0; i < 10 - percent / 10; i++)
                                                m += "▁";
                                            m += "]";
                                            await _bot.EditMessageTextAsync(e.Message.Chat, msg.MessageId,
                                                "Downloading file on server:\n" + m, ParseMode.Default, true,
                                                inlineKeyboardCancelBtn);
                                        }

                                        lastTimeDownloaded = downloaded;
                                        await Task.Delay(1000);
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                    wc.Dispose();
                                    await _bot.EditMessageTextAsync(e.Message.Chat, msg.MessageId,
                                        "Canceled");
                                    d.Delete(true);
                                    _downloadList.TryRemove(cancelTokenId, out _);
                                    return;
                                }
                                catch (Exception ex)
                                {
                                    wc.Dispose();
                                    Log("Error downloading " + e.Message.Text + ": " + ex.Message);
                                    await _bot.EditMessageTextAsync(e.Message.Chat, msg.MessageId,
                                        "Error downloading " + e.Message.Text);
                                    d.Delete(true);
                                    _downloadList.TryRemove(cancelTokenId, out _);
                                    return;
                                }
                                wc.Dispose();

                                if (size < MaxTelegramSize) //Send the file directly
                                {
                                    await _bot.EditMessageTextAsync(e.Message.Chat, msg.MessageId, "Uploading file");
                                    using (var cancellationTokenSource = new CancellationTokenSource())
                                    using (FileStream fs = File.OpenRead(Path.Combine(_downloadPath, dir,
                                        GetFileNameFromUrl(e.Message.Text))))
                                    using (var client = new HttpClient())
                                    using (var multiForm = new MultipartFormDataContent())
                                    {
                                        bool done = false;
                                        long toUpload = 1, uploaded = 0, lastTimeUploaded = 0;
                                        string lastMsg = "";
                                        //This thread checks cancel status and also reports downloads
                                        _ = Task.Run(() =>
                                        {
                                            while (true)
                                            {
                                                if (done)
                                                    return;
                                                if (_downloadList.TryGetValue(cancelTokenId, out bool downloading))
                                                    if (!downloading)
                                                    {
                                                        cancellationTokenSource.Cancel();
                                                        return;
                                                    }

                                                int percent = (int) ((float) uploaded / toUpload * 100f);
                                                string m = percent + "% Uploaded\n" + BytesToString(uploaded) +
                                                           " from " +
                                                           BytesToString(toUpload) + "  " +
                                                           BytesToString(uploaded - lastTimeUploaded) +
                                                           "/s";
                                                if (m != lastMsg) //Do not edit the message and send exactly the same thing
                                                {
                                                    lastMsg = m;
                                                    m += "\n[";
                                                    for (int i = 0; i < percent / 10; i++)
                                                        m += "█";
                                                    for (int i = 0; i < 10 - percent / 10; i++)
                                                        m += "▁";
                                                    m += "]";
                                                    _bot.EditMessageTextAsync(e.Message.Chat, msg.MessageId,
                                                        "Uploading file on server:\n" + m, ParseMode.Default, true
                                                        , inlineKeyboardCancelBtn);
                                                }

                                                lastTimeUploaded = uploaded;
                                                Thread.Sleep(1000);
                                            }
                                        });

                                        client.Timeout = TimeSpan.FromMilliseconds(-1);

                                        var file = new ProgressableStreamContent(
                                            new StreamContent(fs), (sent, total) =>
                                            {
                                                toUpload = total;
                                                uploaded = sent;
                                            });

                                        multiForm.Add(file, "document", fs.Name); // Add the file

                                        multiForm.Add(new StringContent(e.Message.Chat.Id.ToString()),
                                            "chat_id");
                                        multiForm.Add(new StringContent(e.Message.MessageId.ToString()),
                                            "reply_to_message_id");

                                        try
                                        {
                                            var response = await client.PostAsync(
                                                "https://api.telegram.org/bot" + _config.Token + "/sendDocument",
                                                multiForm, cancellationTokenSource.Token);
                                            if (response.StatusCode != HttpStatusCode.OK)
                                            {
                                                await _bot.SendTextMessageAsync(e.Message.Chat,
                                                    "Error on uploading file. Canceling operation.", ParseMode.Default,
                                                    false,
                                                    false, e.Message.MessageId);
                                            }
                                        }
                                        catch (OperationCanceledException)
                                        {
                                            await _bot.SendTextMessageAsync(e.Message.Chat,
                                                "Canceled", ParseMode.Default, false,
                                                false, e.Message.MessageId);
                                        }
                                        finally
                                        {
                                            done = true;
                                        }
                                    }
                                }
                                else
                                {
                                    await _bot.EditMessageTextAsync(e.Message.Chat, msg.MessageId, "Zipping file");
                                    { // Then zip the file
                                        string lastMsg = "";
                                        bool zipDone = false;
                                        long totalBytes = 1, savedBytes = 0;
                                        _ = Task.Run(() =>
                                        {
                                            using (ZipFile zip = new ZipFile())
                                            {
                                                zip.CompressionLevel = CompressionLevel.Level1;
                                                zip.AddFile(
                                                    Path.Combine(_downloadPath, dir, GetFileNameFromUrl(e.Message.Text)),
                                                    "");
                                                zip.SaveProgress += (o, arg) =>
                                                {
                                                    totalBytes = arg.TotalBytesToTransfer;
                                                    savedBytes = arg.BytesTransferred;
                                                };
                                                zip.MaxOutputSegmentSize = MaxFileSize;
                                                zip.Save(Path.Combine(_downloadPath, dir,
                                                             GetFileNameFromUrl(e.Message.Text)) +
                                                         ".zip");
                                            }

                                            zipDone = true;
                                        });
                                        while(true){
                                            await Task.Delay(1000); //This time at first wait for zip to collect data
                                            if(zipDone)
                                                break;
                                            if (totalBytes != 0)
                                            {
                                                int percent =(int)((float) savedBytes / totalBytes * 100f);
                                                string m = percent + "% Zipped\n" + BytesToString(savedBytes) +
                                                           " from " +
                                                           BytesToString(totalBytes);
                                                if (m != lastMsg) //Do not edit the message and send exactly the same thing
                                                {
                                                    lastMsg = m;
                                                    m += "\n[";
                                                    for (int i = 0; i < percent / 10; i++)
                                                        m += "█";
                                                    for (int i = 0; i < 10 - percent / 10; i++)
                                                        m += "▁";
                                                    m += "]";
                                                    await _bot.EditMessageTextAsync(e.Message.Chat, msg.MessageId,
                                                        "Zipping file:\n" + m);
                                                }
                                            }
                                        }
                                    }

                                    File.Delete(Path.Combine(_downloadPath, dir, GetFileNameFromUrl(e.Message.Text)));
                                    //Send all of the zip files to telegram
                                    var files = Directory.GetFiles(Path.Combine(_downloadPath, dir));
                                    bool[] doneUpload = new bool[files.Length]; //This variable controls the progress publishing stuff for each file
                                    using (var cancellationTokenSource = new CancellationTokenSource())
                                    {
                                        for (int i = 0; i < files.Length; i++)
                                        {
                                            if (_downloadList.TryGetValue(cancelTokenId, out bool downloading))
                                                if (!downloading)
                                                    break;

                                            using (FileStream fs = File.OpenRead(files[i]))
                                            using (var client = new HttpClient())
                                            using (var multiForm = new MultipartFormDataContent())
                                            {
                                                long toUpload = 1, uploaded = 0, lastTimeUploaded = 0;
                                                string lastMsg = "";
                                                //This thread checks cancel status and also reports downloads
                                                _ = Task.Run(() =>
                                                {
                                                    int innerI = i;
                                                    while (true)
                                                    {
                                                        if (doneUpload[innerI])
                                                            return;
                                                        if (_downloadList.TryGetValue(cancelTokenId,
                                                            out bool downloadingInner))
                                                            if (!downloadingInner)
                                                            {
                                                                cancellationTokenSource.Cancel();
                                                                return;
                                                            }

                                                        int percent = (int) ((float) uploaded / toUpload * 100f);
                                                        string m = percent + "% Uploaded\n" + BytesToString(uploaded) +
                                                                   " from " +
                                                                   BytesToString(toUpload) + "  " +
                                                                   BytesToString(uploaded - lastTimeUploaded) +
                                                                   "/s";
                                                        if (m != lastMsg) //Do not edit the message and send exactly the same thing
                                                        {
                                                            lastMsg = m;
                                                            m += "\n[";
                                                            for (int j = 0; j < percent / 10; j++)
                                                                m += "█";
                                                            for (int j = 0; j < 10 - percent / 10; j++)
                                                                m += "▁";
                                                            m += "]";
                                                            _bot.EditMessageTextAsync(e.Message.Chat, msg.MessageId,
                                                                "Uploading file on server (Part " + (innerI + 1) + "/" + files.Length + ") :\n" + m, ParseMode.Default,
                                                                true
                                                                , inlineKeyboardCancelBtn);
                                                        }

                                                        lastTimeUploaded = uploaded;
                                                        Thread.Sleep(1000);
                                                    }
                                                });

                                                client.Timeout = TimeSpan.FromMilliseconds(-1);

                                                var file = new ProgressableStreamContent(
                                                    new StreamContent(fs), (sent, total) =>
                                                    {
                                                        toUpload = total;
                                                        uploaded = sent;
                                                    });

                                                multiForm.Add(file, "document", fs.Name); // Add the file

                                                multiForm.Add(new StringContent(e.Message.Chat.Id.ToString()),
                                                    "chat_id");
                                                multiForm.Add(new StringContent(e.Message.MessageId.ToString()),
                                                    "reply_to_message_id");

                                                try
                                                {
                                                    var response = await client.PostAsync(
                                                        "https://api.telegram.org/bot" + _config.Token +
                                                        "/sendDocument",
                                                        multiForm, cancellationTokenSource.Token);
                                                    if (response.StatusCode != HttpStatusCode.OK)
                                                    {
                                                        await _bot.SendTextMessageAsync(e.Message.Chat,
                                                            "Error on uploading file. Canceling operation.",
                                                            ParseMode.Default,
                                                            false,
                                                            false, e.Message.MessageId);
                                                    }
                                                }
                                                catch (OperationCanceledException)
                                                {
                                                    await _bot.SendTextMessageAsync(e.Message.Chat,
                                                        "Canceled", ParseMode.Default, false,
                                                        false, e.Message.MessageId);
                                                }
                                                finally
                                                {
                                                    doneUpload[i] = true;
                                                }
                                            }
                                        }
                                    }
                                }

                                _downloadList.TryRemove(cancelTokenId, out _);
                                await _bot.DeleteMessageAsync(e.Message.Chat, msg.MessageId);
                                d.Delete(true);
                            }
                            else
                            {
                                await _bot.EditMessageTextAsync(e.Message.Chat, msg.MessageId,
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
            string[] extensions = {".zip", ".pdf", ".gif", ".mp3", ".ogg", ".jpg", ".png", ".mp4"};
            return extensions.Contains(Path.GetExtension(name));
        }

        /// <summary>
        /// Converts byte to human readable amount https://stackoverflow.com/a/4975942/4213397
        /// </summary>
        /// <param name="byteCount"></param>
        /// <returns></returns>
        private static string BytesToString(long byteCount)
        {
            string[] suf = {"B", "KB", "MB", "GB", "TB", "PB", "EB"}; //Longs run out around EB
            if (byteCount == 0)
                return "0" + suf[0];
            long bytes = Math.Abs(byteCount);
            int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
            double num = Math.Round(bytes / Math.Pow(1024, place), 1);
            return (Math.Sign(byteCount) * num).ToString(CultureInfo.InvariantCulture) + suf[place];
        }
    }
}