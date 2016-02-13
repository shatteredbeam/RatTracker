﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RatTracker_WPF.Models.Api;
using RatTracker_WPF.Models.App;
using RatTracker_WPF.Models.Edsm;
using RatTracker_WPF.Models.EDDB;
using RatTracker_WPF.Models.NetLog;
using RatTracker_WPF.Properties;
using SpeechLib;
using WebSocket4Net;
using System.Data;
using log4net;
using log4net.Config;


namespace RatTracker_WPF
{
	/// <summary>
	///     Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window, INotifyPropertyChanged
	{
		public static readonly Brush RatStatusColourPositive = Brushes.LightGreen;
		public static readonly Brush RatStatusColourPending = Brushes.Orange;
		public static readonly Brush RatStatusColourNegative = Brushes.Red;

		private static readonly string edsmURL = "http://www.edsm.net/api-v1/";
        private static readonly ILog logger = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static EdsmCoords fuelumCoords = new EdsmCoords() {X = 42, Y = -711.09375, Z = 39.8125};

		private readonly SpVoice voice = new SpVoice();
		private RootObject activeRescues = new RootObject();
		private APIWorker apworker;
		private string currentSystem;
		private long fileOffset;
		private long fileSize;
		private string logDirectory = Settings.Default.NetLogPath;
		private FileInfo logFile;
		private ClientInfo myClient = new ClientInfo();
		private ICollection<TravelLog> myTravelLog;
		private bool onDuty;
		private Overlay overlay;
		private string parserState = "normal";
		private string scState;
		public bool stopNetLog;
		private Thread threadLogWatcher;
		private FileSystemWatcher watcher;
        public ConnectionInfo conninfo = new ConnectionInfo();

		public MainWindow()
		{
			InitializeComponent();
			CheckLogDirectory();
			DataContext = this;
            logger.Info("---Starting RatTracker---");
		}

		public static ConcurrentDictionary<string, Rat> Rats { get; } = new ConcurrentDictionary<string, Rat>();

        public ConnectionInfo ConnInfo
        {
            get { return conninfo;  }
            set
            {
                conninfo = value;
                NotifyPropertyChanged();
            }
        }
		public ClientInfo MyClient
		{
			get { return myClient; }
			set
			{
				myClient = value;
				NotifyPropertyChanged();
			}
		}

		public event PropertyChangedEventHandler PropertyChanged;

		private void CheckVerboseLogging()
		{
			/* if (CheckStationLogging())
			{
				appendStatus("Elite Dangerous is not logging system names!!! ");
				appendStatus("Add VerboseLogging=\"1\" to <Network> section in your config file and restart your client!");
			} */
		}

		public void AppendStatus(string text)
		{
			if (StatusDisplay.Dispatcher.CheckAccess())
			{
				StatusDisplay.Text += "\n" + text;
				StatusDisplay.ScrollToEnd();
				StatusDisplay.CaretIndex = StatusDisplay.Text.Length;
			}
			else
			{
				StatusDisplay.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action<string>(AppendStatus),
					text);
			}
		}

		public static byte[] StringToByteArray(string hex)
		{
			return Enumerable.Range(0, hex.Length)
				.Where(x => x%2 == 0)
				.Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
				.ToArray();
		}

		private void OnRenamed(object source, RenamedEventArgs e)
		{
			/* Stop watching the renamed file, look for new onChanged. */
		}

		private void ParseFriendsList(string friendsList)
		{
			/* Sanitize the XML, it can break if over 40 friends long or so. */
			string xmlData;
			int count = 0;
			xmlData = friendsList.Substring(friendsList.IndexOf("<") + friendsList.Length);
			logger.Debug("Raw xmlData: " + xmlData);
			try
			{
				XDocument xdoc = XDocument.Parse(friendsList);
				logger.Debug("Successful XML parse.");
				XElement rettest = xdoc.Element("OK");
				if (rettest != null)
					logger.Debug("Last friendslist action: " + xdoc.Element("OK").Value);
				IEnumerable<XElement> friends = xdoc.Descendants("item");
				foreach (XElement friend in friends)
				{
					byte[] byteenc;
					UnicodeEncoding unicoded = new UnicodeEncoding();
					/* string preencode = Regex.Replace(friend.Element("name").Value, ".{2}", "\\x$0"); */
					byteenc = StringToByteArray(friend.Element("name").Value);
					//appendStatus("Friend:" + System.Text.Encoding.UTF8.GetString(byteenc));
					count++;
					if (friend.Element("pending").Value == "1")
					{
						AppendStatus("Pending invite from CMDR " + Encoding.UTF8.GetString(byteenc) + "detected!");
						Dispatcher disp = Dispatcher;
						Brush frbrush = null;
						voice.Speak("You have a pending friend invite from commander " +
									Encoding.UTF8.GetString(byteenc));
						disp.BeginInvoke(DispatcherPriority.Normal, (Action) (() => { frbrush = FrButton.Background; }));
						if (frbrush != Brushes.Green)
						{
							/* Dear gods, you're a cheap hack, aren't you? */
							disp.BeginInvoke(DispatcherPriority.Normal,
								(Action) (() => FrButton.Background = Brushes.Yellow));
						}
					}
				}

				/* Check the OK status field, which can contain useful information on successful FRs. */
				foreach (XElement element in xdoc.Descendants())
				{
					if (element.Name == "OK")
					{
						logger.Debug("Return code: " + xdoc.Element("data").Element("OK").Value);
						if (xdoc.Element("data").Element("OK").Value.Contains("Invitation accepted"))
						{
							AppendStatus("Friend request accepted!");
							voice.Speak("Friend request accepted.");
							Dispatcher disp = Dispatcher;
							disp.BeginInvoke(DispatcherPriority.Normal,
								(Action) (() => FrButton.Background = Brushes.Green));
						}
					}
				}

				AppendStatus("Parsed " + count + " friends in FRXML.");
			}
			catch (Exception ex)
			{
				logger.Fatal("XML Parsing exception:" + ex.Message);
			}
		}

		private void ParseWingInvite(string wingInvite)
		{
			string xmlData;
			xmlData = wingInvite.Substring(wingInvite.IndexOf("<") + wingInvite.Length);
			logger.Debug("Raw xmlData: " + xmlData);
			try
			{
				XDocument xdoc = XDocument.Parse(wingInvite);
				logger.Debug("Successful XML parse.");
				voice.Speak("Wing invite detected.");
				IEnumerable<XElement> wing = xdoc.Descendants("commander");
				foreach (XElement wingdata in wing)
				{
					byte[] byteenc;
					UnicodeEncoding unicoded = new UnicodeEncoding();
					/* string preencode = Regex.Replace(friend.Element("name").Value, ".{2}", "\\x$0"); */
					byteenc = StringToByteArray(wingdata.Element("name").Value);
					AppendStatus("Wingmember:" + Encoding.UTF8.GetString(byteenc));
					if (Encoding.UTF8.GetString(byteenc) == MyClient.ClientName)
					{
						AppendStatus("This data matches our current client! Storing information...");
						MyClient.ClientId = wingdata.Element("id").Value;
						AppendStatus("Wingmember IP data:" + xdoc.Element("connectionDetails"));
						string wingIPPattern = "IP4NAT:([0-9.]+):\\d+\\,";
						Match wingMatch = Regex.Match(wingInvite, wingIPPattern, RegexOptions.IgnoreCase);
						if (wingMatch.Success)
						{
							AppendStatus("Successful IP data match: " + wingMatch.Groups[1]);
							MyClient.ClientIp = wingMatch.Groups[1].Value;
						}

						/* If the friend request matches the client name, store his session ID. */
						MyClient.ClientId = wingdata.Element("commander_id").Value;
						MyClient.SessionId = wingdata.Element("session_runid").Value;
					}
				}
			}
			catch (Exception ex)
			{
				logger.Fatal("Error in parseWingInvite: " + ex.Message);
			}
		}

		private void MainWindow_Closing(object sender, CancelEventArgs e)
		{
            stopNetLog = true;
            apworker.DisconnectWs();
			// Clean up our threads and exit.
		}

		private async void CheckLogDirectory()
		{
			if (logDirectory == null | logDirectory == "")
			{
				MessageBox.Show("Error: No log directory is specified, please do so before attempting to go on duty.");
				return;
			}

			if (!Directory.Exists(Settings.Default.NetLogPath))
			{
				MessageBox.Show("Error: Couldn't find E:D Netlog directory: " + Settings.Default.NetLogPath +
								". Please ensure that it is correct in Settings.");
				return;
			}

			StatusDisplay.Text = "Beginning to watch " + logDirectory + " for changes...";
			if (watcher == null)
			{
				watcher = new FileSystemWatcher();
				watcher.Path = logDirectory;
				watcher.NotifyFilter = NotifyFilters.LastAccess | NotifyFilters.LastWrite | NotifyFilters.FileName |
										NotifyFilters.DirectoryName | NotifyFilters.Size;
				watcher.Filter = "*.log";
				watcher.Changed += OnChanged;
				watcher.Created += OnChanged;
				watcher.Deleted += OnChanged;
				watcher.Renamed += OnRenamed;
				watcher.EnableRaisingEvents = true;
			}

			DirectoryInfo tempDir = new DirectoryInfo(logDirectory);
			logFile = (from f in tempDir.GetFiles("*.log") orderby f.LastWriteTime descending select f).First();
			AppendStatus("Started watching file " + logFile.FullName);
			CheckClientConn(logFile.FullName);
			List<KeyValuePair<string, string>> logindata = new List<KeyValuePair<string, string>>();
			logindata.Add(new KeyValuePair<string, string>("email", "mecha@squeak.net"));
			logindata.Add(new KeyValuePair<string, string>("password", "password"));
			apworker = new APIWorker();
			logger.Debug("Call to APIworker returning :" + apworker.connectAPI());
			object col = await apworker.sendAPI("login", logindata);
			AppendStatus("Login returned: " + col);
			apworker.InitWs();
			apworker.OpenWs();
			ReadLogfile(logFile.FullName);
			apworker.ws.MessageReceived += websocketClient_MessageReceieved;
			myTravelLog = new List<TravelLog>();
		}

		/* Moved WS connection to the apworker, but to actually parse the messages we have to hook the event
         * handler here too.
         */

		private void websocketClient_MessageReceieved(object sender, MessageReceivedEventArgs e)
		{
			dynamic data = JsonConvert.DeserializeObject(e.Message);
			switch ((string) data.type)
			{
				case "welcome":
					logger.Info("API MOTD: " + data.data);
					break;
				case "assignment":
					logger.Debug("Got a new assignment datafield: " + data.data);
					break;
				case "test":
					/* This is our echo chamber for WS before it actually does anything useful.
                     */
					AppendStatus("Test data from WS receieved: " + data.data);
					break;
				default:
					logger.Info("Unknown API type field: " + data.type + ": " + data.data);
					break;
			}
		}

		private void ProcessAPIResponse(IAsyncResult result)
		{
			this.AppendStatus("Whaddaya know, ProcessAPIResponse got called!");
		}

		private void CheckClientConn(string lf)
		{
			bool stopSnooping = false;
			AppendStatus("Detecting client connectivity...");
			try
			{
				Dispatcher disp = Dispatcher;
				using (
					StreamReader sr =
						new StreamReader(new FileStream(lf, FileMode.Open, FileAccess.Read,
							FileShare.ReadWrite | FileShare.Delete)))
				{
					int count = 0;
					while (stopSnooping != true && sr.Peek() != -1 && count < 10000)
					{
						count++;
						string line = sr.ReadLine();
						// TODO: Populate WAN, STUN and Turn server labels. Make cleaner TURN detection.
                        if(line.Contains("Local machine is"))
                        {
                            logger.Info("My RunID: " + line.Substring(line.IndexOf("is ")));
                            ConnInfo.runID = line.Substring(line.IndexOf("is "));
                        }
                        if (line.Contains("RxRoute")) // Yes, this early in the netlog, I figure we can just parse the RxRoute without checking for ID. Don't do this later though.
                        {
                            string rxpattern = "IP4NAT:(\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})+:(\\d{1,5}),(\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})+:(\\d{1,5}),(\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})+:(\\d{1,5}),(\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})+:(\\d{1,5}),(\\d),(\\d),(\\d),(\\d{1,4})";
                            Match match = Regex.Match(line, rxpattern, RegexOptions.IgnoreCase);
                            if (match.Success)
                            {
                                logger.Info("Route info: WAN:" + match.Groups[1].Value + " port " + match.Groups[2].Value + ", LAN:" + match.Groups[3].Value + " port " + match.Groups[4].Value + ", STUN: " + match.Groups[5].Value + ":" + match.Groups[6].Value + ", TURN: " + match.Groups[7].Value + ":" + match.Groups[8].Value +
                                    " MTU: " + match.Groups[12].Value + " NAT type: " + match.Groups[9].Value);
                                ConnInfo.WANAddress = match.Groups[1].Value + ":" + match.Groups[2].Value;
                                ConnInfo.MTU = Int32.Parse(match.Groups[12].Value);
                                ConnInfo.NATType = (NATType)Enum.Parse(typeof(NATType),match.Groups[9].Value);
                                ConnInfo.TURNServer = match.Groups[7].Value + ":" + match.Groups[8].Value;
                            }
                        }
						if (line.Contains("failed to initialise upnp"))
						{
							AppendStatus(
								"CRITICAL: E:D has failed to establish a upnp port mapping, but E:D is configured to use upnp. Disable upnp in netlog if you have a router that can't do UPnP, and forward ports manually.");
						}
						if (line.Contains("Sync Established"))
						{
							AppendStatus("Sync Established.");
							disp.BeginInvoke(DispatcherPriority.Normal,
								(Action) (() => SyncButton.Background = Brushes.Green));
						}

						if (line.Contains("ConnectToServerActivity:StartRescueServer"))
						{
							AppendStatus(
								"E:D has established a connection and client is in main menu. Ending early netlog parse.");
							stopSnooping = true;
						}
					}

					AppendStatus("Parsed " + count + " lines to derive client info.");
					if (stopSnooping == false)
					{
						AppendStatus(
							"Client connectivity detection complete. You have a direct port mapped address that E:D can use, and should be connectable.");
					}
				}
			}
			catch (Exception ex)
			{
				logger.Fatal("Exception in checkClientConn:" + ex.Message);
			}
		}

		private void ReadLogfile(string lf)
		{
			try
			{
				using (
					StreamReader sr =
						new StreamReader(new FileStream(lf, FileMode.Open, FileAccess.Read,
							FileShare.ReadWrite | FileShare.Delete)))
				{
					if (fileOffset == 0L)
					{
						logger.Debug("First peek...");
						if (sr.BaseStream.Length > 5000)
						{
							sr.BaseStream.Seek(-5000, SeekOrigin.End);
							/* First peek into the file, rewind a bit and scan from there. */
						}
					}
					else
					{
						sr.BaseStream.Seek(this.fileOffset, SeekOrigin.Begin);
					}

					while (sr.Peek() != -1)
					{
						string line = sr.ReadLine();
						ParseLine(line);
					}

					//appendStatus("Parsed " + count + " new lines. Old fileOffset was "+fileOffset+" and length was "+logFile.Length);
				}
			}
			catch (Exception ex)
			{
				logger.Fatal("Exception in readLogFile: " + ex.Message);
			}
		}

		private void ParseLine(string line)
		{
			/* if (parserState == "ISLAND")
			{
				if (line.Contains(myClient.sessionID.ToString()) && scState=="Normalspace")
				{
					appendStatus("Normalspace Instance match! " + line);
					Dispatcher disp = Dispatcher;
					disp.BeginInvoke(DispatcherPriority.Normal, (Action)(() => wrButton.Background = Brushes.Green));
					//voice.Speak("Successful normal space instance with client.");
				}
			} */
			string reMatchSystem = ".*?(System:).*?\\(((?:[^)]+)).*?\\)";
			Match match = Regex.Match(line, reMatchSystem, RegexOptions.IgnoreCase);
			if (match.Success)
			{
				AppendStatus("System change: " + match.Groups[2].Value + ".");
				TriggerSystemChange(match.Groups[2].Value);
			}

			string reMatchPlayer = "\\{.+\\} (\\d+) x (\\d+).*\\(\\(([0-9.]+):\\d+\\)\\)Name (.+)$";
			Match frmatch = Regex.Match(line, reMatchPlayer, RegexOptions.IgnoreCase);
			if (frmatch.Success)
			{
				AppendStatus("Successful identity match! ID: " + frmatch.Groups[1] + " IP:" + frmatch.Groups[3]);
			}
            string reMatchStats = "machines=(\\d+)&numturnlinks=(\\d+)&backlogtotal=(\\d+)&backlogmax=(\\d+)&avgsrtt=(\\d+)&loss=([0-9]*(?:\\.[0-9]*)+)&&jit=([0-9]*(?:\\.[0-9]*)+)&act1=([0-9]*(?:\\.[0-9]*)+)&act2=([0-9]*(?:\\.[0-9]*)+)"; //FDev, stahp! Why you make me do this shit?!
            Match statmatch = Regex.Match(line, reMatchStats, RegexOptions.IgnoreCase);
            if (statmatch.Success)
            {
                AppendStatus("Updating connection statistics.");
                ConnInfo.Srtt = Int32.Parse(statmatch.Groups[5].Value);
                ConnInfo.Loss = float.Parse(statmatch.Groups[6].Value);
                ConnInfo.Jitter = float.Parse(statmatch.Groups[7].Value);
                ConnInfo.Act1 = float.Parse(statmatch.Groups[8].Value);
                ConnInfo.Act2 = float.Parse(statmatch.Groups[9].Value);
                Dispatcher disp = Dispatcher;
                disp.BeginInvoke(DispatcherPriority.Normal, (Action) (()=> connectionStatus.Text = "SRTT: " + conninfo.Srtt.ToString() + " Jitter: " + conninfo.Jitter.ToString() + " Loss: " + conninfo.Loss.ToString() + " In: " + conninfo.Act1.ToString() + " Out: " + conninfo.Act2.ToString()));
            }
			if (line.Contains("<data>"))
			{
				AppendStatus("Line sent to XML parser");
				ParseFriendsList(line);
			}
			if (line.Contains("<FriendWingInvite>"))
			{
				AppendStatus("Wing invite detected, parsing...");
				ParseWingInvite(line);
				Dispatcher disp = Dispatcher;
				disp.BeginInvoke(DispatcherPriority.Normal, (Action) (() => WrButton.Background = Brushes.Yellow));
			}
			if (line.Contains("JoinSession:WingSession:") && line.Contains(MyClient.ClientIp))
			{
				AppendStatus("Prewing communication underway...");
			}

			if (line.Contains("TalkChannelManager::OpenOutgoingChannelTo") && line.Contains(MyClient.ClientIp))
			{
				AppendStatus("Wing established, opening voice comms.");
				//voice.Speak("Wing established.");
				Dispatcher disp = Dispatcher;
				disp.BeginInvoke(DispatcherPriority.Normal, (Action) (() => WrButton.Background = Brushes.Green));
			}

			if (line.Contains("ListenResponse->Listening (SUCCESS: User has responded via local talkchannel)"))
			{
				AppendStatus("Voice communications established.");
			}

			if (line.Contains("NormalFlight") && scState == "Supercruise")
			{
				scState = "Normalspace";
				AppendStatus("Drop to normal space detected.");
				//voice.Speak("Dropping to normal space.");
			}

			if (line.Contains("Supercruise") && scState == "Normalspace")
			{
				scState = "Supercruise";
				AppendStatus("Entering supercruise.");
				//voice.Speak("Entering supercruise.");
			}
			if (line.Contains("JoinSession:BeaconSession") && line.Contains(MyClient.ClientIp))
			{
				AppendStatus("Client's Beacon in sight.");
			}
		}

		private async void TriggerSystemChange(string value)
		{
			Dispatcher disp = Dispatcher;
			if (value == currentSystem)
			{
				return;
			}
			try
			{
				using (HttpClient client = new HttpClient())
				{
					UriBuilder content = new UriBuilder(edsmURL + "systems?sysname=" + value + "&coords=1") {Port = -1};
					NameValueCollection query = HttpUtility.ParseQueryString(content.Query);
					content.Query = query.ToString();
					logger.Debug("Built query string:" + content);
					HttpResponseMessage response = await client.GetAsync(content.ToString());
					response.EnsureSuccessStatusCode();
					string responseString = await response.Content.ReadAsStringAsync();
					logger.Debug("Response string:" + responseString);
					NameValueCollection temp = new NameValueCollection();
					IEnumerable<EdsmSystem> m = JsonConvert.DeserializeObject<IEnumerable<EdsmSystem>>(responseString);
					//voice.Speak("Welcome to " + value);
					EdsmSystem firstsys = m.FirstOrDefault();
					// EDSM should return the closest lexical match as the first element. Trust that - for now.
					if (firstsys.Name == value)
					{
						if (firstsys.Coords == default(EdsmCoords))
							logger.Debug("Got a match on " + firstsys.Name + " but it has no coords.");
						else
							logger.Debug("Got definite match in first pos, disregarding extra hits:" + firstsys.Name + " X:" +
										firstsys.Coords.X + " Y:" + firstsys.Coords.Y + " Z:" + firstsys.Coords.Z);
						//AppendStatus("Got M:" + firstsys.name + " X:" + firstsys.coords.x + " Y:" + firstsys.coords.y + " Z:" + firstsys.coords.z);
						myTravelLog.Add(new TravelLog() {system = firstsys, lastvisited = DateTime.Now});
						// Should we add systems even if they don't exist in EDSM? Maybe submit them?
					}
					currentSystem = value;
					await disp.BeginInvoke(DispatcherPriority.Normal, (Action) (() => SystemNameLabel.Content = value));
					if (responseString.Contains("-1"))
					{
						await
							disp.BeginInvoke(DispatcherPriority.Normal,
								(Action) (() => SystemNameLabel.Foreground = Brushes.Red));
					}
					else
					{
						await
							disp.BeginInvoke(DispatcherPriority.Normal,
								(Action) (() => SystemNameLabel.Foreground = Brushes.Yellow));
					}
					if (responseString.Contains("coords"))
					{
						await
							disp.BeginInvoke(DispatcherPriority.Normal,
								(Action) (() => SystemNameLabel.Foreground = Brushes.Green));
						logger.Debug("Getting distance from fuelum to " + firstsys.Name);
						string distance = CalculateEDSMDistance("Fuelum", firstsys.Name).ToString();
						await
							disp.BeginInvoke(DispatcherPriority.Normal, (Action) (() => distanceLabel.Content = distance + "LY from Fuelum"));
					}
				}
			}
			catch (Exception ex)
			{
				logger.Fatal("Exception in triggerSystemChange: " + ex.Message);
			}
		}

		private void OnChanged(object source, FileSystemEventArgs e)
		{
			logFile = new FileInfo(e.FullPath);
			/* Handle changed events */
		}

		private void button_Click(object sender, RoutedEventArgs e)
		{
			if (onDuty == false)
			{
				Button.Content = "On Duty";
				onDuty = true;
				watcher.EnableRaisingEvents = true;
				AppendStatus("Started watching for events in netlog.");
				Button.Background = Brushes.Green;
				stopNetLog = false;
				threadLogWatcher = new Thread(NetLogWatcher);
				threadLogWatcher.Name = "Netlog watcher";
				threadLogWatcher.Start();
			}
			else
			{
				Button.Content = "Off Duty";
				onDuty = false;
				watcher.EnableRaisingEvents = false;
				AppendStatus("\nStopped watching for events in netlog.");
				Button.Background = Brushes.Red;
				stopNetLog = true;
			}
		}

		private void NetLogWatcher()
		{
			AppendStatus("Netlogwatcher started.");
			bool logChanged = false;
			try
			{
				while (!stopNetLog)
				{
					Thread.Sleep(2000);

					if (logChanged == false)
					{
						FileInfo fi = new FileInfo(logFile.FullName);
						if (fi.Length != fileSize)
						{
							ReadLogfile(fi.FullName); /* Maybe a poke on the FS is enough to wake watcher? */
							fileOffset = fi.Length;
							fileSize = fi.Length;
						}
					}
				}
			}
			catch (Exception ex)
			{
				logger.Debug("Netlog exception: " + ex.Message);
			}
		}

		private void Main_Menu_Click(object sender, RoutedEventArgs e)
		{
			/* Fleh? */
		}


		private void currentButton_Click(object sender, RoutedEventArgs e)
		{
			AppendStatus("Setting client location to current system: Fuelum");
			SystemName.Text = "Fuelum";
		}

		private async void updateButton_Click(object sender, RoutedEventArgs e)
		{
			logger.Debug("Trying to fetch rescues...");
			Dictionary<string, string> data = new Dictionary<string, string>();
			//data.Add("rats", "56a8fcc7abdd7cc91123fd25");
			data.Add("open", "true");
			string col = await apworker.queryAPI("rescues", data);

			if (col == null)
			{
				logger.Debug("No COL returned from Rescues.");
			}
			else
			{
				logger.Debug("Got a COL from Rescues query!");
				RootObject rescues = JsonConvert.DeserializeObject<RootObject>(col);
				await GetMissingRats(rescues);
				logger.Debug($"Got {rescues.Data.Count} open rescues.");

				RescueGrid.ItemsSource = rescues.Data;
				RescueGrid.AutoGenerateColumns = false;

				foreach (DataGridColumn column in RescueGrid.Columns)
				{
					logger.Debug("Column:" + column.Header);
					if ((string) column.Header == "rats")
					{
						logger.Debug("It's the rats.");
					}
				}
			}
		}
        private void RescueGrid_SelectionChanged(object sender, EventArgs e)
        {
            Datum myrow = (Datum)RescueGrid.SelectedItem;
            logger.Debug("Client is " + myrow.Client.CmdrName);
            ClientName.Text = myrow.Client.CmdrName;
            SystemName.Text = myrow.System;
        }

		private async Task GetMissingRats(RootObject rescues)
		{
			IEnumerable<string> ratIdsToGet = new List<string>();

			IEnumerable<List<string>> datas = rescues.Data.Select(d => d.Rats);
			ratIdsToGet = datas.Aggregate(ratIdsToGet, (current, list) => current.Concat(list));
			ratIdsToGet = ratIdsToGet.Distinct().Except(Rats.Values.Select(x => x._Id));

			foreach (string ratId in ratIdsToGet)
			{
				string response =
					await apworker.queryAPI("rats", new Dictionary<string, string> {{"_id", ratId}, {"limit", "1"}});
				JObject jsonRepsonse = JObject.Parse(response);
				List<JToken> tokens = jsonRepsonse["data"].Children().ToList();
				Rat rat = JsonConvert.DeserializeObject<Rat>(tokens[0].ToString());
				Rats.TryAdd(ratId, rat);

				logger.Debug("Got name for " + ratId + ": " + rat.CmdrName);
			}
		}

		private void startButton_Click(object sender, RoutedEventArgs e)
		{
			AppendStatus("Started tracking new client " + ClientName.Text);
			// TODO myClient.ClientName = ClientName.Text;
			FrButton.Background = Brushes.Red;
			WrButton.Background = Brushes.Red;
			InstButton.Background = Brushes.Red;
			BcnButton.Background = Brushes.Red;
			FueledButton.Background = Brushes.Red;
		}

		private void MenuItem_Click(object sender, RoutedEventArgs e)
		{
			wndSettings swindow = new wndSettings();
			swindow.Show();
		}

		public IEnumerable<EdsmSystem> QueryEDSMSystem(string system)
		{
			try
			{
				using (HttpClient client = new HttpClient())
				{
					UriBuilder content = new UriBuilder(edsmURL + "systems?sysname=" + system + "&coords=1") {Port = -1};
					AppendStatus("Querying EDSM for " + system);
					NameValueCollection query = HttpUtility.ParseQueryString(content.Query);
					content.Query = query.ToString();
					HttpResponseMessage response = client.GetAsync(content.ToString()).Result;
					response.EnsureSuccessStatusCode();
					string responseString = response.Content.ReadAsStringAsync().Result;
					//AppendStatus("Got response: " + responseString);
					if (responseString == "-1")
						return new List<EdsmSystem>() {};
					NameValueCollection temp = new NameValueCollection();
					IEnumerable<EdsmSystem> m = JsonConvert.DeserializeObject<IEnumerable<EdsmSystem>>(responseString);
					return m;
				}
			}
			catch (Exception ex)
			{
				logger.Fatal("Exception in QueryEDSMSystem: " + ex.Message);
				return new List<EdsmSystem>() {};
			}
		}

		public IEnumerable<EdsmSystem> GetCandidateSystems(string target)
		{
			IEnumerable<EdsmSystem> candidates;
			IEnumerable<EdsmSystem> finalcandidates = new List<EdsmSystem>();
			string sysmatch = "([A-Z][A-Z]-[A-z]+) ([a-zA-Z])+(\\d+(?:-\\d+)+?)";
			Match mymatch = Regex.Match(target, sysmatch, RegexOptions.IgnoreCase);
			candidates = QueryEDSMSystem(target.Substring(0, target.IndexOf(mymatch.Groups[3].Value)));
			logger.Debug("Candidate count is " + candidates.Count().ToString() + " from a subgroup of " + mymatch.Groups[3].Value);
			finalcandidates = candidates.Where(x => x.Coords != null);
			logger.Debug("FinalCandidates with coords only is size " + finalcandidates.Count());
			if (finalcandidates.Count() < 1)
			{
				logger.Debug("No final candidates, widening search further...");
				candidates = QueryEDSMSystem(target.Substring(0, target.IndexOf(mymatch.Groups[2].Value)));
				finalcandidates = candidates.Where(x => x.Coords != null);
				if (finalcandidates.Count() < 1)
				{
					logger.Debug("Still nothing! Querying whole sector.");
					candidates = QueryEDSMSystem(target.Substring(0, target.IndexOf(mymatch.Groups[1].Value)));
					finalcandidates = candidates.Where(x => x.Coords != null);
				}
			}
			return finalcandidates;
		}

		/* Attempts to calculate a distance in lightyears between two given systems.
        * This is done using EDSM coordinates.
        * TODO: Once done with testing, transition source to be a <List>TravelLog.
        * TODO: Wait - this is not smart. CalculateEDSMDistance should just give you
        * the distances between those two systems, not do any stunts with the source.
        * Move this functionality out of there, and leave CED requiring a valid source
        * coord - maybe even require source to be type EDSMCoords.
        */

		public double CalculateEDSMDistance(string source, string target)
		{
			EdsmCoords sourcecoords = new EdsmCoords();
			EdsmCoords targetcoords = new EdsmCoords();
			IEnumerable<EdsmSystem> candidates;
			if (source == target)
				return 0; /* Well, it COULD happen? People have been known to do stupid things. */
			foreach (TravelLog mysource in myTravelLog.Reverse())
			{
				if (mysource.system.Coords == null)
				{
					logger.Debug("System in travellog has no coords:" + mysource.system.Name);
				}
				else
				{
					logger.Debug("Found coord'ed system " + mysource.system.Name + ", using as source.");
					sourcecoords = mysource.system.Coords;
				}
			}
			if (sourcecoords == null || source == "Fuelum")
			{
				AppendStatus("Search for travellog coordinated system failed, using Fuelum coords");
				// Add a static Fuelum system reference so we don't have to query EDSM for it.
				sourcecoords = fuelumCoords;
			}
			candidates = QueryEDSMSystem(target);
			if (candidates == null || candidates.Count() < 1)
			{
				logger.Debug("EDSM does not know that system. Widening search...");
				candidates = GetCandidateSystems(target);
			}
			if (candidates.FirstOrDefault().Coords == null)
			{
				logger.Debug("Known system, but no coords. Widening search...");
				candidates = GetCandidateSystems(target);
			}
			if (candidates == null || candidates.Count() < 1)
			{
				//Still couldn't find something, abort.
				AppendStatus("Couldn't find a candidate system, aborting...");
				return -1;
			}
			else
			{
				AppendStatus("I got " + candidates.Count() + " systems with coordinates. Picking the first.");
				targetcoords = candidates.FirstOrDefault().Coords;
			}
			if (sourcecoords != null && targetcoords != null)
			{
				logger.Debug("We have two sets of coords that we can use to find a distance.");
				double deltaX = sourcecoords.X - targetcoords.X;
				double deltaY = sourcecoords.Y - targetcoords.Y;
				double deltaZ = sourcecoords.Z - targetcoords.Z;
				double distance = (double) Math.Sqrt(deltaX*deltaX + deltaY*deltaY + deltaZ*deltaZ);
				AppendStatus("Distance should be " + distance.ToString());
				return distance;
			}
			else
			{
				AppendStatus("Failed to find target coords. Giving up.");
				return -1;
			}
		}

		private async void button_Click_1(object sender, RoutedEventArgs e)
		{
			//TriggerSystemChange("Lave");
			//TriggerSystemChange("Blaa Hypai AI-I b26-1");
			//DateTime testdate = DateTime.Now;
/*            myTravelLog.Add(new TravelLog{ system=new EDSMSystem(){ name = "Sol" }, lastvisited=testdate});
			myTravelLog.Add(new TravelLog { system = new EDSMSystem() { name = "Fuelum" }, lastvisited = testdate});
			myTravelLog.Add(new TravelLog { system = new EDSMSystem() { name= "Leesti" }, lastvisited = testdate}); */
			//AppendStatus("Travellog now contains " + myTravelLog.Count() + " systems. Timestamp of first is " + myTravelLog.First().lastvisited +" name "+myTravelLog.First().system.name);
			//CalculateEDSMDistance("Sol", SystemName.Text);
			OverlayMessage mymessage = new OverlayMessage();
			mymessage.line1header = "Nearest station:";
			mymessage.line1content = "Wollheim Vision, Fuelum (0LY)";
			mymessage.line2header = "Pad size:";
			mymessage.line2content = "Large";
			mymessage.line3header = "Capabilities:";
			mymessage.line3content = "Refuel, Rearm, Repair";
            if(overlay != null)
    			overlay.Queue_Message(mymessage, 30);
			EDDBData edworker = new EDDBData();
			string status = await edworker.UpdateEDDBData();
			AppendStatus("EDDB: " + status);

			EDDBSystem eddbSystem = edworker.systems.First(s => s.name == "Fuelum");
			var station = edworker.GetClosestStation(new EdsmCoords {X = eddbSystem.x, Y = eddbSystem.y, Z = eddbSystem.z});
			AppendStatus("Closest system to 'Fuelum' is '" + eddbSystem.name +
						"', closest station to star with known coordinates (should be 'Wollheim Vision') is '" + station.name + "'.");
		}

		private void MenuItem_Click_1(object sender, RoutedEventArgs e)
		{
			//open the dispatch interface
			DispatchInterface.DispatchMain dlg = new DispatchInterface.DispatchMain();
			dlg.Show();
		}

		private void OverlayMenu_Click(object sender, RoutedEventArgs e)
		{
            if (overlay == null)
            {
                overlay = new Overlay();
                overlay.SetCurrentClient(MyClient);
                overlay.Show();
                IEnumerable<Monitor> monitors = Monitor.AllMonitors;
                foreach (Monitor mymonitor in monitors)
                {
                    if (mymonitor.IsPrimary == true)
                    {
                        overlay.Left = mymonitor.Bounds.Right - overlay.Width;
                        overlay.Top = mymonitor.Bounds.Top;
                    }
                }
                overlay.Topmost = true;
                HotKeyHost hotKeyHost = new HotKeyHost((HwndSource)PresentationSource.FromVisual(Application.Current.MainWindow));
                hotKeyHost.AddHotKey(new CustomHotKey("ToggleOverlay", Key.O, ModifierKeys.Control | ModifierKeys.Alt, true));
                hotKeyHost.HotKeyPressed += handleHotkeyPress;
            }
            else
                overlay.Close();
		}

		private void handleHotkeyPress(object sender, HotKeyEventArgs e)
		{
			logger.Debug("Hotkey pressed: " + Name + e.HotKey.Key.ToString());
			if (e.HotKey.Key == Key.O)
			{
				if (overlay.Visibility == Visibility.Hidden)
					overlay.Visibility = Visibility.Visible;
				else
					overlay.Visibility = Visibility.Hidden;
			}
		}

		private void App_Deactivated(object sender, EventArgs e)
		{
			if (overlay != null)
			{
				overlay.Topmost = true;
			}
		}

		protected virtual void NotifyPropertyChanged([CallerMemberName] string propertyName = null)
		{
			PropertyChangedEventHandler onPropertyChanged = PropertyChanged;
			onPropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}

		/// 
		/// Rat-Button click handlers
		/// TODO review api messages
		/// 
		private void frButton_Click(object sender, RoutedEventArgs e)
		{
			IDictionary<string, string> data = new Dictionary<string, string>();
			RatState ratState = GetRatStateForButton(sender, FrButton, FrButton_Copy, FrButton_Copy1);

			switch (ratState.FriendRequest)
			{
				case RequestState.NotRecieved:
					ratState.FriendRequest = RequestState.Recieved;
					AppendStatus("Sending Friend Request acknowledgement.");
					data.Add("ReceivedFR", "true");
					apworker.SendWs("FriendRequest", data);
					break;
				case RequestState.Recieved:
					ratState.FriendRequest = RequestState.Accepted;
					break;
				case RequestState.Accepted:
					AppendStatus("Cancelling FR status.");
					data.Add("ReceivedFR", "false");
					apworker.SendWs("FriendsRequest", data);
					ratState.FriendRequest = RequestState.NotRecieved;
					break;
				default:
					throw new ArgumentOutOfRangeException();
			}
		}

		private void wrButton_Click(object sender, RoutedEventArgs e)
		{
			IDictionary<string, string> data = new Dictionary<string, string>();
			RatState ratState = GetRatStateForButton(sender, WrButton, WrButton_Copy, WrButton_Copy1);

			switch (ratState.WingRequest)
			{
				case RequestState.NotRecieved:
					ratState.WingRequest = RequestState.Recieved;
					AppendStatus("Sending Wing Request acknowledgement.");
					data.Add("ReceivedWR", "true");
					apworker.SendWs("WingRequest", data);
					break;
				case RequestState.Recieved:
					ratState.WingRequest = RequestState.Accepted;
					break;
				case RequestState.Accepted:
					ratState.WingRequest = RequestState.NotRecieved;
					AppendStatus("Cancelled WR status.");
					data.Add("ReceivedWR", "false");
					apworker.SendWs("WingRequest", data);
					break;
				default:
					throw new ArgumentOutOfRangeException();
			}
		}

		private void sysButton_Click(object sender, RoutedEventArgs e)
		{
			RatState ratState = GetRatStateForButton(sender, SysButton, SysButton_Copy, SysButton_Copy1);
			if (ratState.InSystem)
			{
				AppendStatus("Sending System acknowledgement.");
			}
			else
			{
				AppendStatus("Cancelling System status.");
			}

			ratState.InSystem = !ratState.InSystem;
		}

		private void bcnButton_Click(object sender, RoutedEventArgs e)
		{
			RatState ratState = GetRatStateForButton(sender, BcnButton, BcnButton_Copy, BcnButton_Copy1);
			if (ratState.Beacon)
			{
				AppendStatus("Sending Beacon acknowledgement.");
			}
			else
			{
				AppendStatus("Cancelling Beacon status.");
			}

			ratState.Beacon = !ratState.Beacon;
		}

		private void instButton_Click(object sender, RoutedEventArgs e)
		{
			RatState ratState = GetRatStateForButton(sender, InstButton, InstButton_Copy, InstButton_Copy1);
			if (ratState.InInstance)
			{
				AppendStatus("Sending Good Instance message.");
			}
			else
			{
				AppendStatus("Cancelling Good instance message.");
			}

			ratState.InInstance = !ratState.InInstance;
		}

		private RatState GetRatStateForButton(object sender, Button selfButton, Button rat2Button, Button rat3Button)
		{
			RatState ratState;
			if (Equals(sender, selfButton))
			{
				ratState = MyClient.Self;
			}
			else if (Equals(sender, rat2Button))
			{
				ratState = MyClient.Rat2;
			}
			else if (Equals(sender, rat3Button))
			{
				ratState = MyClient.Rat3;
			}
			else
			{
				ratState = MyClient.Self;
			}

			return ratState;
		}


		private void fueledButton_Click(object sender, RoutedEventArgs e)
		{
			if (Equals(FueledButton.Background, Brushes.Red))
			{
				AppendStatus("Reporting fueled status, requesting paperwork link...");
				FueledButton.Background = Brushes.Green;
				/* image.Source = new BitmapImage(RatTracker_WPF.Properties.Resources.yellow_light); */
			}
			else
			{
				AppendStatus("Fueled status now negative.");
				FueledButton.Background = Brushes.Red;
			}
			logger.Debug("Sending fake rescue request!");
			IDictionary<string, string> req = new Dictionary<string, string>();
			req.Add("open", "true");
			//req.Add("_id", myRescue.id); /* TODO: Must hold a handle to my rescue ID somewhere to identify for API interaction */
			apworker.SendWs("rescues", req);
		}

		[Serializable]
		public class CustomHotKey : HotKey
		{
			private string name;

			public CustomHotKey(string name, Key key, ModifierKeys modifiers, bool enabled) : base(key, modifiers, enabled)
			{
				Name = name;
			}

			public string Name
			{
				get { return name; }
				set
				{
					if (value != name)
					{
						name = value;
						OnPropertyChanged(name);
					}
				}
			}
		}
	}
}