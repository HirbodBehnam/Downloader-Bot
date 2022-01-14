using DownloaderBot;
using Ionic.Zip;
using Ionic.Zlib;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Extensions.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InputFiles;
using Telegram.Bot.Types.ReplyMarkups;

internal class ConfigStruct
{
#pragma warning disable CS0649
	public string Token;
	public string DownloadPath;
	public int[] Admins;
	public long MaxFileSize;
#pragma warning restore CS0649
}

class Program
{
	private const int
		MaxFileSize = 1000 * 1000 * 50,
		MaxTelegramSize =
			20 * 1000 * 1000; // For some reasons, looks like there is some problems with 1024 * 1024 * 50 

	private const string Version = "1.3.0";
	private static ConfigStruct _config;
	private static string _downloadPath;
	private static bool _freeBot;
	private static ConcurrentDictionary<Guid, CancellationTokenSource> _downloadList; // True is downloading, false is canceled
	private static readonly Random rng = new();

	static async Task Main(string[] args)
	{
		Console.WriteLine("Downloader Bot Version " + Version);
		// Load the config file
		try
		{
			string configText = await System.IO.File.ReadAllTextAsync(args.Length == 0 ? "config.json" : args[0]);
			_config = JsonConvert.DeserializeObject<ConfigStruct>(configText);
		}
		catch (Exception ex)
		{
			Console.WriteLine("Error: " + ex.Message);
			Environment.Exit(1);
		}

		_freeBot = _config.Admins == null || _config.Admins.Length == 0;
		// Create download folder
		_downloadPath = string.IsNullOrWhiteSpace(_config.DownloadPath)
			? Environment.CurrentDirectory
			: _config.DownloadPath;
		Directory.CreateDirectory(_downloadPath);
		// Setup the cancel button list
		_downloadList = new();
		// Setup the bot
		var cancelToken = new CancellationTokenSource();
		var bot = new TelegramBotClient(_config.Token);
		Util.Log("Authorized on bot " + (await bot.GetMeAsync()).Username);
		ReceiverOptions receiverOptions = new() { AllowedUpdates = new UpdateType[] { UpdateType.Message, UpdateType.CallbackQuery } };
		bot.StartReceiving(HandleUpdate, HandleErrorAsync, receiverOptions, cancelToken.Token);
		var mutex = new SemaphoreSlim(0);
		Console.CancelKeyPress += (o, e) =>
		{
			cancelToken.Cancel();
			mutex.Release();
		};
		while (true)
			await mutex.WaitAsync();
	}

	public static Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
	{
		var ErrorMessage = exception switch
		{
			ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
			_ => exception.ToString()
		};

		Util.Log(ErrorMessage);
		return Task.CompletedTask;
	}

	public static Task HandleUpdate(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
	{
		var handler = update.Type switch
		{
			UpdateType.Message => BotOnMessage(botClient, update),
			UpdateType.CallbackQuery => BotOnCallbackQueryReceived(update.CallbackQuery!),
			_ => Task.CompletedTask
		};

		new Task(async () =>
		{
			try
			{
				await handler;
			}
			catch (Exception exception)
			{
				await HandleErrorAsync(botClient, exception, cancellationToken);
			}
		}).Start();

		return Task.CompletedTask;
	}

	private static async Task BotOnMessage(ITelegramBotClient bot, Update update)
	{
		if (update.Message.Text == null)
			return;
		switch (update.Message.Text)
		{
			case "/start":
				await bot.SendTextMessageAsync(update.Message.Chat, "Welcome!\nJust send the link to the bot.");
				return;
			case "/id":
				await bot.SendTextMessageAsync(update.Message.Chat, update.Message.From.Id.ToString());
				return;
		}
		if (!_freeBot && !Array.Exists(_config.Admins, id => id == update.Message.From.Id))
		{
			Util.Log("Unauthorized access from user " + update.Message.From.FirstName + " " +
				update.Message.From.LastName + "; ID: " + update.Message.From.Id);
			return;
		}
		// At first check if the url is valid
		if (!Util.ValidateUrl(update.Message.Text))
		{
			await bot.SendTextMessageAsync(update.Message.Chat, "The URL is not valid.");
			return;
		}

		var msg = await bot.SendTextMessageAsync(update.Message.Chat, "Getting some info about file...",
			replyToMessageId: update.Message.MessageId);
		// Then check the file size; If it is less than 20MB use telegram itself
		long size = await Util.GetSizeOfFile(update.Message.Text);
		if (size < 1) // Either an error or file size is really 0
		{
			await bot.EditMessageTextAsync(update.Message.Chat, msg.MessageId,
				"Error on getting file size or the file size is 0");
			return;
		}

		if (size < MaxTelegramSize && Util.CheckExtenstion(Util.GetFileNameFromUrl(update.Message.Text)))
		{
			await bot.EditMessageTextAsync(update.Message.Chat, msg.MessageId,
				"Telegram is directly downloading the file...");
			try
			{
				InputOnlineFile inputOnlineFile = new(update.Message.Text);
				await bot.SendDocumentAsync(update.Message.Chat, inputOnlineFile,
					replyToMessageId: update.Message.MessageId);
				await bot.DeleteMessageAsync(update.Message.Chat, msg.MessageId);
				return; // If the file is uploaded, do not continue to download it
			}
			catch (Exception ex)
			{
				Util.Log("Error: Cannot download directly from Telegram:" + ex.Message);
			}
		}

		if (size < _config.MaxFileSize) // Download the file, zip it and send it
		{
			Guid cancelTokenId = Guid.NewGuid();
			// Create cancel button
			var inlineKeyboardCancelBtn = new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("Cancel", cancelTokenId.ToString()));
			using var cancellationTokenSource = new CancellationTokenSource();
			if (!_downloadList.TryAdd(cancelTokenId, cancellationTokenSource))
				inlineKeyboardCancelBtn = null;
			// Now create directory and file for it
			string dir = rng.Next().ToString();
			var d = Directory.CreateDirectory(Path.Combine(_downloadPath, dir));
			try
			{
				using HttpClient client = new();
				using FileStream file = new(Path.Combine(_downloadPath, dir, Util.GetFileNameFromUrl(update.Message.Text)), FileMode.Create, FileAccess.Write, FileShare.None);
				await client.DownloadAsync(update.Message.Text, file, new ProgressReporter(bot, update.Message.Chat, msg.MessageId, inlineKeyboardCancelBtn, "Downloading file on server:"), cancellationTokenSource.Token);
				if (cancellationTokenSource.IsCancellationRequested)
					throw new OperationCanceledException();
			}
			catch (OperationCanceledException)
			{
				await bot.EditMessageTextAsync(update.Message.Chat, msg.MessageId,
					"Canceled");
				d.Delete(true);
				_downloadList.TryRemove(cancelTokenId, out _);
				return;
			}
			catch (Exception ex)
			{
				Util.Log("Error downloading " + update.Message.Text + ": " + ex.Message);
				await bot.EditMessageTextAsync(update.Message.Chat, msg.MessageId,
					"Error downloading " + update.Message.Text);
				d.Delete(true);
				_downloadList.TryRemove(cancelTokenId, out _);
				return;
			}

			if (size < MaxTelegramSize) // Send the file directly
			{
				await bot.EditMessageTextAsync(update.Message.Chat, msg.MessageId, "Uploading file");
				using FileStream fs = System.IO.File.OpenRead(Path.Combine(_downloadPath, dir,
					Util.GetFileNameFromUrl(update.Message.Text)));
				using var client = new HttpClient();
				using var multiForm = new MultipartFormDataContent();
				client.Timeout = TimeSpan.FromMilliseconds(-1);
				var file = new ProgressableStreamContent(
					new StreamContent(fs), new ProgressReporter(bot, update.Message.Chat, msg.MessageId, inlineKeyboardCancelBtn, "Uploading to Telegram..."));

				multiForm.Add(file, "document", fs.Name); // Add the file
				multiForm.Add(new StringContent(update.Message.Chat.Id.ToString()),
					"chat_id");
				multiForm.Add(new StringContent(update.Message.MessageId.ToString()),
					"reply_to_message_id");

				try
				{
					var response = await client.PostAsync(
						"https://api.telegram.org/bot" + _config.Token + "/sendDocument",
						multiForm, cancellationTokenSource.Token);
					if (response.StatusCode != HttpStatusCode.OK)
					{
						await bot.SendTextMessageAsync(update.Message.Chat,
							"Error on uploading file. Canceling operation.", replyToMessageId: update.Message.MessageId);
					}
				}
				catch (OperationCanceledException)
				{
					await bot.SendTextMessageAsync(update.Message.Chat,
						"Canceled", replyToMessageId: update.Message.MessageId);
				}
			}
			else
			{
				await bot.EditMessageTextAsync(update.Message.Chat, msg.MessageId, "Zipping file");
				// Then zip the file
				await Task.Run(() =>
				{
					var progressReporter = new ProgressReporter(bot, update.Message.Chat, msg.MessageId, null, "Zipping...");
					using ZipFile zip = new();
					zip.CompressionLevel = CompressionLevel.Level1;
					zip.AddFile(
						Path.Combine(_downloadPath, dir, Util.GetFileNameFromUrl(update.Message.Text)),
						"");
					zip.SaveProgress += (o, arg) => progressReporter.Report(new ProgressData(arg.TotalBytesToTransfer, arg.BytesTransferred));
					zip.MaxOutputSegmentSize = MaxFileSize;
					zip.Save(Path.Combine(_downloadPath, dir,
								 Util.GetFileNameFromUrl(update.Message.Text)) +
							 ".zip");
				});
				System.IO.File.Delete(Path.Combine(_downloadPath, dir, Util.GetFileNameFromUrl(update.Message.Text)));
				// Send all of the zip files to telegram
				var files = Directory.GetFiles(Path.Combine(_downloadPath, dir));
				for (int i = 0; i < files.Length; i++)
				{
					using FileStream fs = System.IO.File.OpenRead(files[i]);
					using var client = new HttpClient();
					using var multiForm = new MultipartFormDataContent();

					client.Timeout = TimeSpan.FromMilliseconds(-1);

					var file = new ProgressableStreamContent(
						new StreamContent(fs), new ProgressReporter(bot, update.Message.Chat, msg.MessageId, inlineKeyboardCancelBtn, $"Uploading part {i+1}/{files.Length} to Telegram..."));

					multiForm.Add(file, "document", fs.Name); // Add the file
					multiForm.Add(new StringContent(update.Message.Chat.Id.ToString()),
						"chat_id");
					multiForm.Add(new StringContent(update.Message.MessageId.ToString()),
						"reply_to_message_id");

					try
					{
						var response = await client.PostAsync(
							"https://api.telegram.org/bot" + _config.Token +
							"/sendDocument",
							multiForm, cancellationTokenSource.Token);
						if (response.StatusCode != HttpStatusCode.OK)
						{
							_ = await bot.SendTextMessageAsync(update.Message.Chat,
								"Error on uploading file. Canceling operation.",
								replyToMessageId: update.Message.MessageId);
						}
					}
					catch (OperationCanceledException)
					{
						await bot.SendTextMessageAsync(update.Message.Chat,
							"Canceled",replyToMessageId: update.Message.MessageId);
					}
				}
			}

			_downloadList.TryRemove(cancelTokenId, out _);
			await bot.DeleteMessageAsync(update.Message.Chat, msg.MessageId);
			d.Delete(true);
		}
		else
		{
			await bot.EditMessageTextAsync(update.Message.Chat, msg.MessageId,
				"File is too large for bot! (file size is " + size + " bytes)");
		}
	}

	private static Task BotOnCallbackQueryReceived(CallbackQuery callbackQuery)
	{
		var guid = Guid.Parse(callbackQuery.Data);
		if (_downloadList.TryGetValue(guid, out var cancel))
			cancel.Cancel();
		return Task.CompletedTask;
	}
}