using System;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace DownloaderBot
{
	/// <summary>
	/// This struct contains the info needed to report the progress data of a download
	/// </summary>
	/// <param name="Total">Total amount of bytes to read</param>
	/// <param name="CurrentRead">How much we have read until now</param>
	public record struct ProgressData(long Total, long CurrentRead)
	{
		/// <summary>
		/// The precentage that we have read
		/// </summary>
		public int Percent { get => (int)(CurrentRead * 100 / Total); }
	};

	/// <summary>
	/// Progress reporter reports the progress for ProgressData in telegram bot
	/// </summary>
	public class ProgressReporter : IProgress<ProgressData>
	{
		private long _lastReadAmount = 0;
		/// <summary>
		/// After then we can report the progress
		/// </summary>
		private DateTime _nextReport;
		/// <summary>
		/// The bot
		/// </summary>
		private readonly ITelegramBotClient _botClient;
		/// <summary>
		/// The chat for <see cref="_messageID"/>
		/// </summary>
		private readonly Chat _chat;
		/// <summary>
		/// The message to edit
		/// </summary>
		private readonly int _messageID;
		/// <summary>
		/// The button to send each time
		/// </summary>
		private readonly InlineKeyboardMarkup _inlineKeyboardMarkup;
		/// <summary>
		/// What we should send before the message
		/// </summary>
		private readonly string _messagePrefix;
		private readonly object locker = new();
		/// <summary>
		/// Creates a new progress reporter for 
		/// </summary>
		/// <param name="client">The telegram bot client</param>
		/// <param name="chat">The chat of the user</param>
		/// <param name="messageID">The message ID to edit</param>
		/// <param name="inlineKeyboardMarkup">The buttons to send to user</param>
		/// <param name="messagePrefix">What should become before our status message</param>
		public ProgressReporter(ITelegramBotClient client, Chat chat, int messageID, InlineKeyboardMarkup inlineKeyboardMarkup, string messagePrefix)
		{
			_botClient = client;
			_chat = chat;
			_messageID = messageID;
			_inlineKeyboardMarkup = inlineKeyboardMarkup;
			_messagePrefix = messagePrefix + "\n";
			_nextReport = DateTime.Now;
		}
		public void Report(ProgressData value)
		{
			if (value.Total == 0)
				return;
			// Do not report if it itsn't the time
			string downloadSpeed;
			lock (locker)
			{
				var now = DateTime.Now;
				if (now < _nextReport)
					return;
				downloadSpeed = Util.BytesToString((long)((value.CurrentRead - _lastReadAmount) / (now - _nextReport.Subtract(TimeSpan.FromSeconds(1))).TotalSeconds));
				_nextReport = now.AddSeconds(1);
				_lastReadAmount = value.CurrentRead;
			}
			// Otherwise we should report!
			string m = value.Percent + "% Completed\n" + Util.BytesToString(value.CurrentRead) + " from " +
												   Util.BytesToString(value.Total) + "  " +
												   downloadSpeed + "/s";
			// Send and forget
			new Task(async () =>
			{
				await _botClient.EditMessageTextAsync(_chat, _messageID,
											   _messagePrefix + m, replyMarkup: _inlineKeyboardMarkup);
			}).Start();
		}
	}
}