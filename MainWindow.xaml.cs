﻿namespace StockSharp.Qsh2StockSharp
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using System.Threading.Tasks;
	using System.Windows;
	using System.Windows.Controls;

	using Ecng.Common;
	using Ecng.Collections;
	using Ecng.Serialization;
	using Ecng.Xaml;

	using MoreLinq;

	using QScalp;
	using QScalp.History;
	using QScalp.History.Reader;

	using StockSharp.Algo;
	using StockSharp.Algo.Storages;
	using StockSharp.BusinessEntities;
	using StockSharp.Logging;
	using StockSharp.Messages;
	using StockSharp.Xaml;

	using Security = StockSharp.BusinessEntities.Security;

	public partial class MainWindow
	{
		private class Settings
		{
			public string QshFolder { get; set; }
			public string StockSharpFolder { get; set; }
			public StorageFormats Format { get; set; }
			public string Board { get; set; }
			public string SecurityLike { get; set; }
		}

		private readonly SecurityIdGenerator _idGenerator = new SecurityIdGenerator();
		private readonly LogManager _logManager = new LogManager();

		private bool _isStarted;

		private static readonly string _settingsFile = Path.Combine("Settings", "settings.xml");
		private static readonly string _convertedFilesFile = Path.Combine("Settings", "converted_files.txt");

		private readonly HashSet<string> _convertedFiles = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

		public MainWindow()
		{
			InitializeComponent();

			Directory.CreateDirectory("Settings");

			_logManager.Listeners.Add(new GuiLogListener(LogControl));
			_logManager.Listeners.Add(new FileLogListener { LogDirectory = "Logs", SeparateByDates = SeparateByDateModes.FileName });

			Format.SetDataSource<StorageFormats>();
			Format.SetSelectedValue<StorageFormats>(StorageFormats.Binary);

			Board.Boards.AddRange(ExchangeBoard.EnumerateExchangeBoards().Where(b => b.Exchange == Exchange.Moex));
            Board.SelectedBoard = ExchangeBoard.Forts;

            try
			{
				if (File.Exists(_settingsFile))
				{
					var settings = new XmlSerializer<Settings>().Deserialize(_settingsFile);

					QshFolder.Folder = settings.QshFolder;
					StockSharpFolder.Folder = settings.StockSharpFolder;
					Format.SetSelectedValue<StorageFormats>(settings.Format);
					SecurityLike.Text = settings.SecurityLike;
					Board.SelectedBoard =
						settings.Board.IsEmpty()
							? ExchangeBoard.Forts
							: Board.Boards.FirstOrDefault(b => b.Code.CompareIgnoreCase(settings.Board)) ?? ExchangeBoard.Forts;
				}

				if (File.Exists(_convertedFilesFile))
				{
					_convertedFiles.AddRange(File.ReadAllLines(_convertedFilesFile));
				}
			}
			catch (Exception ex)
			{
				ex.LogError();
			}
		}

		protected override void OnClosed(EventArgs e)
		{
			SaveSettings();
			base.OnClosed(e);
		}

		private Settings SaveSettings()
		{
			var settings = new Settings
			{
				QshFolder = QshFolder.Folder,
				StockSharpFolder = StockSharpFolder.Folder,
				Format = Format.GetSelectedValue<StorageFormats>() ?? StorageFormats.Binary,
				SecurityLike = SecurityLike.Text,
				Board = Board.SelectedBoard?.Code
			};

			try
			{
				new XmlSerializer<Settings>().Serialize(settings, _settingsFile);
			}
			catch (Exception ex)
			{
				ex.LogError();
			}

			return settings;
		}

		private void LockControls(bool isEnabled)
		{
			QshFolder.IsEnabled = StockSharpFolder.IsEnabled = Format.IsEnabled = Board.IsEnabled = SecurityLike.IsEnabled = isEnabled;
		}

		private void Convert_OnClick(object sender, RoutedEventArgs e)
		{
			Convert.IsEnabled = false;

			if (_isStarted)
			{
				_logManager.Application.AddInfoLog("Остановка конвертации.");
				_isStarted = false;
				return;
			}

			LockControls(false);

			_logManager.Application.AddInfoLog("Запуск конвертации.");
			_isStarted = true;

			var settings = SaveSettings();
			var board = Board.SelectedBoard;

			Task.Factory.StartNew(() =>
			{
				var registry = new StorageRegistry();
				((LocalMarketDataDrive)registry.DefaultDrive).Path = settings.StockSharpFolder;

				this.GuiAsync(() =>
				{
					Convert.Content = "Остановить";
					Convert.IsEnabled = true;
				});

				ConvertDirectory(settings.QshFolder, registry, settings.Format, board, settings.SecurityLike);
			})
			.ContinueWith(t =>
			{
				Convert.Content = "Запустить";
				Convert.IsEnabled = true;

				LockControls(true);

				if (t.IsFaulted)
				{
					t.Exception.LogError();

					new MessageBoxBuilder()
						.Text("В процессе конвертации произошла ошибка. Ошибка записана в лог.")
						.Error()
						.Owner(this)
						.Show();

					return;
				}

				new MessageBoxBuilder()
					.Text("Конвертация {0}.".Put(_isStarted ? "выполнена" : "остановлена"))
					.Owner(this)
					.Show();

			}, TaskScheduler.FromCurrentSynchronizationContext());
		}

		private void ConvertDirectory(string path, IStorageRegistry registry, StorageFormats format, ExchangeBoard board, string securityLike)
		{
			if (!_isStarted)
				return;

			Directory.GetFiles(path, "*.qsh").ForEach(f => ConvertFile(f, registry, format, board, securityLike));
			Directory.GetDirectories(path).ForEach(d => ConvertDirectory(d, registry, format, board, securityLike));
		}

		private void ConvertFile(string fileName, IStorageRegistry registry, StorageFormats format, ExchangeBoard board, string securityLike)
		{
			if (!_isStarted)
				return;

			var fileNameKey = format + "_" + fileName;

			if (_convertedFiles.Contains(fileNameKey))
				return;

			_logManager.Application.AddInfoLog("Конвертация файла {0}.", fileName);

			var isExact = !securityLike.EndsWith("*");

			const int maxBufCount = 1000;

			var data = new Dictionary<Security, Tuple<List<QuoteChangeMessage>, List<ExecutionMessage>, List<Level1ChangeMessage>, List<ExecutionMessage>>>();

			using (var qr = QshReader.Open(fileName))
			{
				var currentDate = qr.CurrentDateTime;

				for (var i = 0; i < qr.StreamCount; i++)
				{
					var stream = (ISecurityStream)qr[i];
					var security = GetSecurity(stream.Security, board);
					var priceStep = security.PriceStep ?? 1;
					var securityId = security.ToSecurityId();
					var lastTransactionId = 0L;

					if (!securityLike.IsEmpty() &&
						(isExact
							? !securityId.SecurityCode.CompareIgnoreCase(securityLike)
							: !securityId.SecurityCode.ContainsIgnoreCase(securityLike)))
						continue;

					var secData = data.SafeAdd(security, key => Tuple.Create(new List<QuoteChangeMessage>(), new List<ExecutionMessage>(), new List<Level1ChangeMessage>(), new List<ExecutionMessage>()));

					switch (stream.Type)
					{
						case StreamType.Stock:
						{
							((IStockStream)stream).Handler += (key, quotes, spread) =>
							{
								var quotes2 = quotes.Select(q =>
								{
									Sides side;

									switch (q.Type)
									{
										case QuoteType.Unknown:
										case QuoteType.Free:
										case QuoteType.Spread:
											throw new ArgumentException(q.Type.ToString());
										case QuoteType.Ask:
										case QuoteType.BestAsk:
											side = Sides.Sell;
											break;
										case QuoteType.Bid:
										case QuoteType.BestBid:
											side = Sides.Buy;
											break;
										default:
											throw new ArgumentOutOfRangeException();
									}

									return new QuoteChange(side, priceStep * q.Price, q.Volume);
								}).ToArray();

								var md = new QuoteChangeMessage
								{
									SecurityId = securityId,
									ServerTime = currentDate.ApplyTimeZone(TimeHelper.Moscow),
									Bids = quotes2.Where(q => q.Side == Sides.Buy),
									Asks = quotes2.Where(q => q.Side == Sides.Sell),
								};

								//if (md.Verify())
								//{
								secData.Item1.Add(md);

								if (secData.Item1.Count > maxBufCount)
								{
									registry.GetQuoteMessageStorage(security, format: format).Save(secData.Item1);
									secData.Item1.Clear();
								}
								//}
								//else
								//	_logManager.Application.AddErrorLog("Стакан для {0} в момент {1} не прошел валидацию. Лучший бид {2}, Лучший офер {3}.", security, qr.CurrentDateTime, md.BestBid, md.BestAsk);
							};
							break;
						}
						case StreamType.Deals:
						{
							((IDealsStream)stream).Handler += deal =>
							{
								secData.Item2.Add(new ExecutionMessage
								{
									ExecutionType = ExecutionTypes.Tick,
									SecurityId = securityId,
									OpenInterest = deal.OI == 0 ? (long?)null : deal.OI,
									ServerTime = deal.DateTime.ApplyTimeZone(TimeHelper.Moscow),
									Volume = deal.Volume,
									TradeId = deal.Id == 0 ? (long?)null : deal.Id,
									TradePrice = (decimal)deal.Price,
									OriginSide = 
										deal.Type == DealType.Buy
											? Sides.Buy
											: (deal.Type == DealType.Sell ? Sides.Sell : (Sides?)null)
								});

								if (secData.Item2.Count > maxBufCount)
								{
									registry.GetTickMessageStorage(security, format: format).Save(secData.Item2);
									secData.Item2.Clear();
								}
							};
							break;
						}
						case StreamType.OrdLog:
						{
							((IOrdLogStream)stream).Handler += (key, ol) =>
							{
								var currTransactionId = ol.DateTime.Ticks;

								if (lastTransactionId < currTransactionId)
									lastTransactionId = currTransactionId;
								else if (lastTransactionId >= currTransactionId)
									lastTransactionId++;

								var msg = new ExecutionMessage
								{
									ExecutionType = ExecutionTypes.OrderLog,
									SecurityId = securityId,
									OpenInterest = ol.OI == 0 ? (long?)null : ol.OI,
									OrderId = ol.OrderId,
									OrderPrice = priceStep * ol.Price,
									ServerTime = ol.DateTime.ApplyTimeZone(TimeHelper.Moscow),
									Volume = ol.Amount,
									Balance = ol.AmountRest,
									TradeId = ol.DealId == 0 ? (long?)null : ol.DealId,
									TradePrice = ol.DealPrice == 0 ? (decimal?)null : priceStep * ol.DealPrice,
									TransactionId = lastTransactionId
								};

								var status = 0;

								if (ol.Flags.Contains(OrdLogFlags.Add))
								{
									msg.OrderState = OrderStates.Active;
								}
								else if (ol.Flags.Contains(OrdLogFlags.Fill))
								{
									msg.OrderState = OrderStates.Done;
								}
								else if (ol.Flags.Contains(OrdLogFlags.Canceled))
								{
									msg.OrderState = OrderStates.Done;
									status |= 0x200000;
								}
								else if (ol.Flags.Contains(OrdLogFlags.CanceledGroup))
								{
									msg.OrderState = OrderStates.Done;
									status |= 0x400000;
								}
								else if (ol.Flags.Contains(OrdLogFlags.Moved))
								{
									status |= 0x100000;
								}

								if (ol.Flags.Contains(OrdLogFlags.Buy))
								{
									msg.Side = Sides.Buy;
								}
								else if (ol.Flags.Contains(OrdLogFlags.Sell))
								{
									msg.Side = Sides.Sell;
								}

								if (ol.Flags.Contains(OrdLogFlags.FillOrKill))
								{
									msg.TimeInForce = TimeInForce.MatchOrCancel;
									status |= 0x00080000;
								}

								if (ol.Flags.Contains(OrdLogFlags.Quote))
								{
									msg.TimeInForce = TimeInForce.PutInQueue;
									status |= 0x01;
								}

								if (ol.Flags.Contains(OrdLogFlags.Counter))
								{
									status |= 0x02;
								}

								if (ol.Flags.Contains(OrdLogFlags.CrossTrade))
								{
									status |= 0x20000000;
								}

								if (ol.Flags.Contains(OrdLogFlags.NonSystem))
								{
									msg.IsSystem = false;
									status |= 0x04;
								}

								if (ol.Flags.Contains(OrdLogFlags.EndOfTransaction))
								{
									status |= 0x1000;
								}

								msg.OrderStatus = (OrderStatus)status;

								secData.Item4.Add(msg);

								if (secData.Item4.Count > maxBufCount)
								{
									registry.GetOrderLogMessageStorage(security, format: format).Save(secData.Item4);
									secData.Item4.Clear();
								}
							};
							break;
						}
						case StreamType.AuxInfo:
						{
							((IAuxInfoStream)stream).Handler += (key, info) =>
							{
								secData.Item3.Add(new Level1ChangeMessage
								{
									SecurityId = securityId,
									ServerTime = info.DateTime.ApplyTimeZone(TimeHelper.Moscow),
								}
								.TryAdd(Level1Fields.LastTradePrice, priceStep * info.Price)
								.TryAdd(Level1Fields.BidsVolume, (decimal)info.BidTotal)
								.TryAdd(Level1Fields.AsksVolume, (decimal)info.AskTotal)
								.TryAdd(Level1Fields.HighPrice, priceStep * info.HiLimit)
								.TryAdd(Level1Fields.LowPrice, priceStep * info.LoLimit)
								.TryAdd(Level1Fields.OpenInterest, (decimal)info.OI));

								if (secData.Item3.Count > maxBufCount)
								{
									registry.GetLevel1MessageStorage(security, format: format).Save(secData.Item3);
									secData.Item3.Clear();
								}
							};
							break;
						}
						case StreamType.Orders:
						case StreamType.Trades:
						case StreamType.Messages:
						case StreamType.None:
						{
							continue;
						}
						default:
							throw new ArgumentOutOfRangeException("Неподдерживаемый тип потока {0}.".Put(stream.Type));
					}
				}

				if (data.Count > 0)
				{
					while (qr.CurrentDateTime != DateTime.MaxValue && _isStarted)
						qr.Read(true);
				}
			}

			if (!_isStarted)
				return;

			foreach (var pair in data)
			{
				if (pair.Value.Item1.Any())
				{
					registry.GetQuoteMessageStorage(pair.Key, format: format).Save(pair.Value.Item1);
				}

				if (pair.Value.Item2.Any())
				{
					registry.GetTickMessageStorage(pair.Key, format: format).Save(pair.Value.Item2);
				}

				if (pair.Value.Item3.Any())
				{
					registry.GetLevel1MessageStorage(pair.Key, format: format).Save(pair.Value.Item3);
				}

				if (pair.Value.Item4.Any())
				{
					registry.GetOrderLogMessageStorage(pair.Key, format: format).Save(pair.Value.Item4);
				}
			}

			if (data.Count > 0)
			{
				File.AppendAllLines(_convertedFilesFile, new[] { fileNameKey });
				_convertedFiles.Add(fileNameKey);
			}
		}

		private Security GetSecurity(QScalp.Security security, ExchangeBoard board)
		{
			return new Security
			{
				Id = _idGenerator.GenerateId(security.Ticker, board),
				Code = security.Ticker,
				Board = board,
				PriceStep = (decimal)security.Step,
				Decimals = security.Precision
			};
		}

		private void TryEnable()
		{
			Convert.IsEnabled = !QshFolder.Folder.IsEmpty() && !StockSharpFolder.Folder.IsEmpty() && Board.SelectedBoard != null;
		}

		private void Board_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			TryEnable();
		}

		private void OnFolderChange(string folder)
		{
			TryEnable();
		}
	}
}