#define USE_SEPARATE_THREADS
#define USE_LOCALTIME
#define USE_L2
using BitMEX;
using CsvHelper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Timers;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WebSocketSharp;
using MetroFramework.Forms;
using System.Globalization;
using System.Reflection;

#pragma warning disable CS1634, CS0168, CS0414
namespace BitMEXAssistant
{
    public partial class Bot : MetroForm
    {
        #region Class Properties

        double heartbeatcheck = 5; // seconds after the last message to send a ping
        bool FirstLoad = true;
        string APIKey = "";
        string APISecret = "";
        BitMEXApi bitmex;
        List<Instrument> ActiveInstruments = new List<Instrument>();
        List<Instrument> AllInstruments = new List<Instrument>();
        Instrument ActiveInstrument = new Instrument();
        int ActiveInstrumentIndex = 0;
        string Timeframe = "1m";
        bool RealNetwork = false;

        string DCASelectedSymbol = "";
        int DCACounter = 0;
        int DCAContractsPer = 0;
        int DCAHours = 0;
        int DCAMinutes = 0;
        int DCASeconds = 0;
        int DCATimes = 0;
        string DCASide = "Buy";

        WebSocket ws_general;
        WebSocket ws_user;

        DateTime GeneralWebScocketLastMessage = new DateTime();
        DateTime UserWebScocketLastMessage = new DateTime();
        Dictionary<string, decimal> Prices = new Dictionary<string, decimal>();
        //List<Alert> Alerts = new List<Alert>();

        public static string Version = "0.0.20";

        string LimitNowBuyOrderId = "";
        decimal LimitNowBuyOrderPrice = 0;
        string LimitNowSellOrderId = "";
        decimal LimitNowSellOrderPrice = 0;
        List<OrderBook> OrderBookTopAsks = new List<OrderBook>();
        List<OrderBook> OrderBookTopBids = new List<OrderBook>();
        Position SymbolPosition = new Position();
        decimal Balance = 0;

        #region L2
        bool L2Initialized = false;
        SortedDictionary<long, OrderBook> OrderBookL2Asks = new SortedDictionary<long, OrderBook>(Comparer<long>.Create((x, y) => y.CompareTo(x)));
        SortedDictionary<long, OrderBook> OrderBookL2Bids = new SortedDictionary<long, OrderBook>();
        #endregion L2


        string TrailingStopMethod = "Limit";

        List<Order> LimitNowBuyOrders = new List<Order>();
        List<Order> LimitNowSellOrders = new List<Order>();

        List<string> LogLines = new List<string>();

        decimal ActiveTickSize = 0m;
        int Decimals = 0;


        EventHandler OnTradeUpdate;
        EventHandler OnOrderUpdate;

        Thread AmendBuyThread;
        Thread AmendSellThread;

        ManualResetEvent UpdateLimitNowBuys = new ManualResetEvent(false);
        ManualResetEvent UpdateLimitNowSells = new ManualResetEvent(false);

        decimal LimitNowBuyTicksFromCenter = decimal.Zero;
        decimal LimitNowSellTicksFromCenter = decimal.Zero;
        string LimitNowBuyMethod = "";
        string LimitNowSellMethod = "";
        decimal LimitNowStopLossSellDelta = decimal.Zero;
        decimal LimitNowStopLossBuyDelta = decimal.Zero;

        int LimitNowBuyLevel = 0;
        int LimitNowSellLevel = 0;

        // reconnect timers
        System.Timers.Timer GeneralWS_ReconnectTimer = new System.Timers.Timer();
        System.Timers.Timer UserWS_ReconnectTimer = new System.Timers.Timer();
        #endregion

        public Bot()
        {
            InitializeComponent();
        }

        #region Bot Form Events

        private void Bot_Load(object sender, EventArgs e)
        {

            APIInfo Login = new APIInfo();

            while (!Login.APIValid)
            {
                Login.ShowDialog();
                if (Login.DialogClosed)
                {
                    this.Close();
                }
                Thread.Sleep(0);
            }


            if (Login.APIValid)
            {
                InitializeDropdownsAndSettings();
                InitializeAPI();

                InitializePostAPIDropdownsAndSettings();


                InitializeSymbolInformation();
                InitializeDependentSymbolInformation();
                InitializePostSymbolInfoSettings();
                
                InitializeWebSocket();
                InitializeWalletWebSocket();

                InitializeVideoPlaylist();


                tmrClientUpdates.Start(); // Start our client update timer
                Heartbeat.Start();
                
            }

        }

        private void InitializeVideoPlaylist()
        {
            if (YouTubeVideoBrowser != null) // the YouTubeVideoBrowser is a member variable of the form
            {
                YouTubeVideoBrowser.Dispose();
            }
                
            YouTubeVideoBrowser = new System.Windows.Forms.WebBrowser();
            YouTubeVideoBrowser.Location = new System.Drawing.Point(0, 0);
            YouTubeVideoBrowser.MinimumSize = new System.Drawing.Size(20, 20);
            YouTubeVideoBrowser.Name = "YouTubeVideoBrowser";
            YouTubeVideoBrowser.Dock = DockStyle.Fill;
            YouTubeVideoBrowser.TabIndex = 0;

            tabVideos.Controls.Add(YouTubeVideoBrowser);

            var embed = "<html><head>" +
            "<meta http-equiv=\"X-UA-Compatible\" content=\"IE=Edge\"/>" +
            "</head><body style=\"background-color: #111111;\">" +
            "<iframe width=\"560\" height=\"315\" src=\"{0}\"" +
            "frameborder = \"0\" allow = \"autoplay; encrypted-media\" allowfullscreen></iframe>" +
            "</body></html>";
            var url = "https://www.youtube.com/embed/videoseries?list=PLM0BBafRCnRNy7aj0ZXy3zsg0HFNj1t0M";
            YouTubeVideoBrowser.DocumentText = string.Format(embed, url);
        }

        private void InitializeWalletWebSocket()
        {
            // Margin Connect - do this last so we already have the price.
            ws_general.Send("{\"op\": \"subscribe\", \"args\": [\"margin\"]}");
        }

        private void Bot_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (ws_general != null)
            {
                ws_general.OnClose -= GeneralWebSocketOnClose;
                ws_general.Close(); // Make sure our websocket is closed.
            }
            if (ws_user != null)
            {
                ws_user.OnClose -= UserWebSocketOnClose;
                ws_user.Close(); // Make sure our websocket is closed.
            }
        }
        #endregion

        #region Logging

        private void Log(string msg)
        {
            LogLines.Add(DateTime.Now.ToString("MM/dd/yyyy hh:mm:ss.fff tt") + ":"+msg);

            ConsoleText.Invoke((MethodInvoker)(() =>
            {
                ConsoleText.Lines = LogLines.ToArray();
            }));
        }
        #endregion

        #region Initialization
        private void GeneralWebSocketOnClose(object sender,CloseEventArgs args)
        {
            Console.WriteLine("General websocket closed:Starting Reconnect timer");
            L2Initialized = false;
            // if it hits here then network was interrupted
            GeneralWS_ReconnectTimer.Interval = 2000; // reconnect in 1 sec
            GeneralWS_ReconnectTimer.AutoReset = false;
            GeneralWS_ReconnectTimer.Start();
        }

        private void UserWebSocketOnClose(object sender, CloseEventArgs args)
        {
            Console.WriteLine("User websocket closed:Starting Reconnect timer");
            // if it hits here then network was interupted
            UserWS_ReconnectTimer.Interval = 2000; // reconnect in 1 sec
            UserWS_ReconnectTimer.AutoReset = false;
            UserWS_ReconnectTimer.Start();
        }

        private int InstrumentIndex(string symbol)
        {
            int index = AllInstruments.FindIndex(x => x.Symbol == symbol);
            return index;
        }
        private void InitializeWebSocket()
        {
            Log("Initializing Websockets");
            
            OnTradeUpdate += LimitNow_TradeUpdate;
            OnOrderUpdate += LimitNow_OrderUpdate;

            //ActiveInstrument = bitmex.GetInstrument(((Instrument)ddlSymbol.SelectedItem).Symbol)[0];

            // we also start the limitnow update threads over here
            AmendBuyThread = new Thread(LimitNowAmendBuyThreadAction);
            AmendSellThread = new Thread(LimitNowAmendSellThreadAction);
            AmendBuyThread.Start();
            AmendSellThread.Start();
            if (Properties.Settings.Default.Network == "Real")
            {
                ws_general = new WebSocket("wss://www.bitmex.com/realtime");
                ws_user = new WebSocket("wss://www.bitmex.com/realtime");
            }
            else
            {
                ws_general = new WebSocket("wss://testnet.bitmex.com/realtime");
                ws_user = new WebSocket("wss://testnet.bitmex.com/realtime");
            }

            GeneralWS_ReconnectTimer.Elapsed += (sender, e) =>
            {
                ws_general.Connect();
            };
            ws_general.OnMessage += (sender, e) =>
            {
                //Log("On message");
                GeneralWebScocketLastMessage = DateTime.UtcNow;
                try
                {
                    if (e.Data == "pong")
                        return;
                    JObject Message = JObject.Parse(e.Data);
                    //Console.WriteLine("General Websocket Message:" + e.Data);
                    if (Message.ContainsKey("table"))
                    {
                        if ((string)Message["table"] == "trade")
                        {
                            if (Message.ContainsKey("data"))
                            {
                                //Console.WriteLine("Trade Data:" + Message["data"]);
                                JArray TD = (JArray)Message["data"];
                                if (TD.Any())
                                {
                                    decimal Price = (decimal)TD.Children().LastOrDefault()["price"];
                                    string Symbol = (string)TD.Children().LastOrDefault()["symbol"];
                                    Prices[Symbol] = Price;
                                    // Necessary for trailing stops
                                    UpdateTrailingStopData(ActiveInstrument.Symbol, Prices[ActiveInstrument.Symbol]);
                                    if (SymbolPosition.Symbol == Symbol && SymbolPosition.CurrentQty != 0 && chkTrailingStopEnabled.Checked)
                                    {
                                        ProcessTrailingStop(Symbol, Price);
                                    }
                                    try
                                    {
                                        //Task.Run(() => { UpdatePrice(); });
                                        string direction = (string)TD.Children().LastOrDefault()["tickDirection"];
                                        Task.Run(() => { UpdatePriceThread(direction); });
                                    }
                                    catch (Exception exUpdate)
                                    {

                                    }
                                    if (OnTradeUpdate!=null)
                                    {
                                        try
                                        {
                                            OnTradeUpdate(this, EventArgs.Empty);
                                        }
                                        catch(Exception OnTradeException)
                                        {
                                            Console.WriteLine("Exception OnTrade");
                                        }
                                    }
                                }
                            }
                        }
                        else if ((string)Message["table"] == "orderBook10")
                        {
                            //Console.WriteLine("orderbook10");
                            if (Message.ContainsKey("data"))
                            {
                                //Console.WriteLine("OB10 Data:"+ Message["data"]);
                                JArray TD = (JArray)Message["data"];
                                if (TD.Any())
                                {
                                    JArray TDBids = (JArray)TD[0]["bids"];
                                    if (TDBids.Any())
                                    {
                                        List<OrderBook> OB = new List<OrderBook>();
                                        foreach (JArray i in TDBids)
                                        {
                                            OrderBook OBI = new OrderBook();
                                            OBI.Price = (decimal)i[0];
                                            OBI.Size = (int)i[1];
                                            OB.Add(OBI);
                                        }

                                        OrderBookTopBids = OB;
                                    }

                                    JArray TDAsks = (JArray)TD[0]["asks"];
                                    if (TDAsks.Any())
                                    {
                                        List<OrderBook> OB = new List<OrderBook>();
                                        foreach (JArray i in TDAsks)
                                        {
                                            OrderBook OBI = new OrderBook();
                                            OBI.Price = (decimal)i[0];
                                            OBI.Size = (int)i[1];
                                            OB.Add(OBI);
                                        }

                                        OrderBookTopAsks = OB;
                                    }
                                    if (OnOrderUpdate != null)
                                    {
                                        try
                                        {
                                            OnOrderUpdate(this, EventArgs.Empty);
                                        }
                                        catch (Exception OnOrderException)
                                        {
                                            Console.WriteLine("Exception OnOrder");
                                        }
                                    }
                                }
                            }
                        }
                        else if ((string)Message["table"] == "orderBookL2")
                        {
                            /*
                            Console.WriteLine("L2:"+e.Data);
                            if (Message.ContainsKey("data"))
                            {
                                Console.WriteLine("OBL2 Data:"+ Message["data"]);
                            }
                            */
                            try
                            {
                                if ((string)Message["action"] != "partial" && !L2Initialized)
                                {
                                    return;
                                }
                                if ((string)Message["action"] == "partial")
                                {
                                    Console.WriteLine("L2 Initializing/ReInitialize");
                                    L2Initialized = true;
                                    OrderBookL2Asks.Clear();
                                    OrderBookL2Bids.Clear();
                                    JArray TD = (JArray)Message["data"];
                                    foreach (JObject i in TD)
                                    {
                                        OrderBook OBI = new OrderBook();
                                        OBI.Price = (decimal)i["price"];
                                        OBI.Size = (int)i["size"];
                                        long ID = (long)i["id"];
                                        if ((string)i["side"] == "Sell")
                                        {
                                            OrderBookL2Asks.Add(ID, OBI);
                                        }
                                        else if ((string)i["side"] == "Buy")
                                        {
                                            OrderBookL2Bids.Add(ID, OBI);
                                        }
                                    }
                                    long IDt = OrderBookL2Bids.ElementAt(0).Key;
                                    decimal Price;
                                    decimal l1 = (decimal)100000000 * (decimal)ActiveInstrumentIndex;
                                    decimal l2 = l1 - (decimal)IDt;
                                    decimal tickSize = ActiveInstrument.TickSize;
                                    if (ActiveInstrument.Symbol == "XBTUSD")
                                    {
                                        tickSize = 0.01M;
                                    }
                                    Price = l2 * tickSize;
                                    //Console.WriteLine("Top bids:"+Price);


                                }
                                else if ((string)Message["action"] == "delete")
                                {
                                    //Console.WriteLine("L2 delete");
                                    JArray TD = (JArray)Message["data"];
                                    foreach (JObject i in TD)
                                    {
                                        long ID = (long)i["id"];
                                        if ((string)i["side"] == "Sell")
                                        {
                                            if (OrderBookL2Asks.ContainsKey(ID))
                                            {
                                                OrderBookL2Asks.Remove(ID);
                                            }
                                            else
                                            {
                                                Console.WriteLine("Unable to find record L2 Ask");
                                            }
                                        }
                                        else if ((string)i["side"] == "Buy")
                                        {
                                            if (OrderBookL2Bids.ContainsKey(ID))
                                            {
                                                OrderBookL2Bids.Remove(ID);
                                            }
                                            else
                                            {
                                                Console.WriteLine("Unable to find record L2 Bids");
                                            }
                                        }
                                    }
                                }
                                else if ((string)Message["action"] == "update")
                                {
                                    //Console.WriteLine("L2 update");
                                    JArray TD = (JArray)Message["data"];
                                    foreach (JObject i in TD)
                                    {
                                        long ID = (long)i["id"];
                                        decimal Price;
                                        decimal l1 = (decimal)100000000 * (decimal)ActiveInstrumentIndex;
                                        decimal l2 = l1 - (decimal)ID;
                                        decimal tickSize = ActiveInstrument.TickSize;
                                        if (ActiveInstrument.Symbol == "XBTUSD")
                                        {
                                            tickSize = 0.01M;
                                        }
                                        Price = l2 * tickSize;
                                        //Console.WriteLine("Updating "+ (string)i["side"] + " Price Level:" + Price +"("+ (int)i["size"] +")");
                                        if ((string)i["side"] == "Sell")
                                        {
                                            if (OrderBookL2Asks.ContainsKey(ID))
                                            {
                                                OrderBookL2Asks[ID].Size = (int)i["size"];
                                            }
                                            else
                                            {
                                                Console.WriteLine("Unable to find record L2 Ask");
                                            }
                                        }
                                        else if ((string)i["side"] == "Buy")
                                        {
                                            if (OrderBookL2Bids.ContainsKey(ID))
                                            {
                                                OrderBookL2Bids[ID].Size = (int)i["size"];
                                            }
                                            else
                                            {
                                                Console.WriteLine("Unable to find record L2 Bids");
                                            }
                                        }
                                    }
                                }
                                else if ((string)Message["action"] == "insert")
                                {
                                    //Console.WriteLine("L2 insert");
                                    JArray TD = (JArray)Message["data"];
                                    foreach (JObject i in TD)
                                    {
                                        OrderBook OBI = new OrderBook();
                                        OBI.Price = (decimal)i["price"];
                                        OBI.Size = (int)i["size"];
                                        long ID = (long)i["id"];
                                        if ((string)i["side"] == "Sell")
                                        {
                                            if (OrderBookL2Asks.ContainsKey(ID))
                                            {
                                                Console.WriteLine("L2 Asks already contains key");
                                            }
                                            else
                                            {
                                                OrderBookL2Asks.Add(ID, OBI);
                                            }
                                        }
                                        else if ((string)i["side"] == "Buy")
                                        {
                                            if (OrderBookL2Bids.ContainsKey(ID))
                                            {
                                                Console.WriteLine("L2 Bids already contains key");
                                            }
                                            else
                                            {
                                                OrderBookL2Bids.Add(ID, OBI);
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception l2)
                            {
                                Console.WriteLine("L2 exception");
                            }
                            //Console.WriteLine("L2: Action:" + Message["action"]);
                            if (OnOrderUpdate != null)
                            {
                                try
                                {
                                    OnOrderUpdate(this, EventArgs.Empty);
                                }
                                catch (Exception OnOrderException)
                                {
                                    Console.WriteLine("Exception OnOrder");
                                }
                            }
                        }
                        else if ((string)Message["table"] == "position")
                        {
                            // PARSE
                            if (Message.ContainsKey("data"))
                            {
                                JArray TD = (JArray)Message["data"];
                                if (TD.Any())
                                {
                                    if (TD.Children().LastOrDefault()["symbol"] != null)
                                    {
                                        SymbolPosition.Symbol = (string)TD.Children().LastOrDefault()["symbol"];
                                    }
                                    if (TD.Children().LastOrDefault()["currentQty"] != null)
                                    {
                                        SymbolPosition.CurrentQty = (int?)TD.Children().LastOrDefault()["currentQty"];

                                    }
                                    if (TD.Children().LastOrDefault()["avgEntryPrice"] != null)
                                    {
                                        SymbolPosition.AvgEntryPrice = (decimal?)TD.Children().LastOrDefault()["avgEntryPrice"];

                                    }
                                    if (TD.Children().LastOrDefault()["markPrice"] != null)
                                    {
                                        SymbolPosition.MarkPrice = (decimal?)TD.Children().LastOrDefault()["markPrice"];

                                    }
                                    if (TD.Children().LastOrDefault()["liquidationPrice"] != null)
                                    {
                                        SymbolPosition.LiquidationPrice = (decimal?)TD.Children().LastOrDefault()["liquidationPrice"];
                                    }
                                    if (TD.Children().LastOrDefault()["leverage"] != null)
                                    {
                                        SymbolPosition.Leverage = (decimal?)TD.Children().LastOrDefault()["leverage"];

                                    }
                                    if (TD.Children().LastOrDefault()["unrealisedPnl"] != null)
                                    {
                                        SymbolPosition.UnrealisedPnl = (decimal?)TD.Children().LastOrDefault()["unrealisedPnl"];
                                    }
                                    if (TD.Children().LastOrDefault()["unrealisedPnlPcnt"] != null)
                                    {
                                        SymbolPosition.UnrealisedPnlPcnt = (decimal?)TD.Children().LastOrDefault()["unrealisedPnlPcnt"];

                                    }

                                }
                            }
                        }
                        else if ((string)Message["table"] == "margin")
                        {
                            if (Message.ContainsKey("data"))
                            {
                                JArray TD = (JArray)Message["data"];
                                if (TD.Any())
                                {
                                    try
                                    {
                                        /*
                                        JToken tok = TD.Children().LastOrDefault();
                                        Console.WriteLine("Last Token:" + tok.ToString());
                                        if (tok["walletBalance"]!=null)
                                        {
                                            Console.WriteLine("Has Wallet balance");
                                        }
                                        */
                                        JToken token = TD.Children().LastOrDefault()["walletBalance"];
                                        if (token != null)
                                        {
                                            Balance = ((decimal)token / 100000000);
                                            UpdateBalanceAndTime();
                                        }
                                        /*
                                        else
                                        {
                                            Console.WriteLine("Unable to get wallet balance? Token is null");
                                        }
                                        */
                                    }
                                    catch (Exception ex)
                                    {

                                    }
                                }
                            }
                        }
                        else if ((string)Message["table"] == "execution")
                        {
                            Console.WriteLine("Websocket execution");
                            if (Message.ContainsKey("data"))
                            {
                                Console.WriteLine("Data:" + Message["data"]);
                            }
                        }
                        else if ((string)Message["table"] == "order")
                        {
                            //Console.WriteLine("Websocket order");
                            if (Message.ContainsKey("data"))
                            {
                                //Console.WriteLine("Order Data:" + Message["data"]);
                            }
                        }
                    }
                    else if (Message.ContainsKey("info") && Message.ContainsKey("docs"))
                    {
                        string WebSocketInfo = "Websocket Info: " + Message["info"].ToString() + " " + Message["docs"].ToString();
                        UpdateWebSocketInfo(WebSocketInfo);
                    }
                }
                catch (Exception ex)
                {
                    //MessageBox.Show(ex.Message);
                }
            };
            ws_general.OnError += (sender, e) =>
            {
                Console.WriteLine("General Websocket on Error");
            };
            ws_general.OnClose += GeneralWebSocketOnClose;
            ws_general.OnOpen += (sender, e) =>
            {
                Console.WriteLine("General websocket on open");
                string APIExpires = bitmex.GetExpiresArg();
                string Signature = bitmex.GetWebSocketSignatureString(APISecret, APIExpires);
                ws_general.Send("{\"op\": \"authKeyExpires\", \"args\": [\"" + APIKey + "\", " + APIExpires + ", \"" + Signature + "\"]}");
                IntializeGeneralWS(FirstLoad);
            };
            ws_general.Connect();

            UserWS_ReconnectTimer.Elapsed += (sender, e) =>
            {
                ws_user.Connect();
            };
            ws_user.OnMessage += (sender, e) =>
            {
                UserWebScocketLastMessage = DateTime.UtcNow;
                try
                {
                    if (e.Data == "pong")
                        return;
                    JObject Message = JObject.Parse(e.Data);
                    //Console.WriteLine("User Websocket Message:" + e.Data);
                    if (Message.ContainsKey("table"))
                    {
                        if ((string)Message["table"] == "trade")
                        {
                            if (Message.ContainsKey("data"))
                            {
                                JArray TD = (JArray)Message["data"];
                                if (TD.Any())
                                {
                                    decimal Price = (decimal)TD.Children().LastOrDefault()["price"];
                                    string Symbol = (string)TD.Children().LastOrDefault()["symbol"];
                                    Prices[Symbol] = Price;

                                    // Necessary for trailing stops
                                    UpdateTrailingStopData(ActiveInstrument.Symbol, Prices[ActiveInstrument.Symbol]);
                                    if (SymbolPosition.Symbol == Symbol && SymbolPosition.CurrentQty != 0 && chkTrailingStopEnabled.Checked)
                                    {
                                        ProcessTrailingStop(Symbol, Price);
                                    }
                                }
                            }
                        }
                        else if ((string)Message["table"] == "orderBook10")
                        {
                            if (Message.ContainsKey("data"))
                            {
                                JArray TD = (JArray)Message["data"];
                                if (TD.Any())
                                {
                                    JArray TDBids = (JArray)TD[0]["bids"];
                                    if (TDBids.Any())
                                    {
                                        List<OrderBook> OB = new List<OrderBook>();
                                        foreach (JArray i in TDBids)
                                        {
                                            OrderBook OBI = new OrderBook();
                                            OBI.Price = (decimal)i[0];
                                            OBI.Size = (int)i[1];
                                            OB.Add(OBI);
                                        }

                                        OrderBookTopBids = OB;
                                    }

                                    JArray TDAsks = (JArray)TD[0]["asks"];
                                    if (TDAsks.Any())
                                    {
                                        List<OrderBook> OB = new List<OrderBook>();
                                        foreach (JArray i in TDAsks)
                                        {
                                            OrderBook OBI = new OrderBook();
                                            OBI.Price = (decimal)i[0];
                                            OBI.Size = (int)i[1];
                                            OB.Add(OBI);
                                        }

                                        OrderBookTopAsks = OB;
                                    }
                                }
                            }
                        }
                        else if ((string)Message["table"] == "position")
                        {
                            // PARSE
                            if (Message.ContainsKey("data"))
                            {
                                JArray TD = (JArray)Message["data"];
                                if (TD.Any())
                                {
                                    if (TD.Children().LastOrDefault()["symbol"] != null)
                                    {
                                        SymbolPosition.Symbol = (string)TD.Children().LastOrDefault()["symbol"];
                                    }
                                    if (TD.Children().LastOrDefault()["currentQty"] != null)
                                    {
                                        SymbolPosition.CurrentQty = (int?)TD.Children().LastOrDefault()["currentQty"];

                                    }
                                    if (TD.Children().LastOrDefault()["avgEntryPrice"] != null)
                                    {
                                        SymbolPosition.AvgEntryPrice = (decimal?)TD.Children().LastOrDefault()["avgEntryPrice"];

                                    }
                                    if (TD.Children().LastOrDefault()["markPrice"] != null)
                                    {
                                        SymbolPosition.MarkPrice = (decimal?)TD.Children().LastOrDefault()["markPrice"];

                                    }
                                    if (TD.Children().LastOrDefault()["liquidationPrice"] != null)
                                    {
                                        SymbolPosition.LiquidationPrice = (decimal?)TD.Children().LastOrDefault()["liquidationPrice"];
                                    }
                                    if (TD.Children().LastOrDefault()["leverage"] != null)
                                    {
                                        SymbolPosition.Leverage = (decimal?)TD.Children().LastOrDefault()["leverage"];

                                    }
                                    if (TD.Children().LastOrDefault()["unrealisedPnl"] != null)
                                    {
                                        SymbolPosition.UnrealisedPnl = (decimal?)TD.Children().LastOrDefault()["unrealisedPnl"];
                                    }
                                    if (TD.Children().LastOrDefault()["unrealisedPnlPcnt"] != null)
                                    {
                                        SymbolPosition.UnrealisedPnlPcnt = (decimal?)TD.Children().LastOrDefault()["unrealisedPnlPcnt"];

                                    }

                                }
                            }
                        }
                        else if ((string)Message["table"] == "margin")
                        {
                            if (Message.ContainsKey("data"))
                            {
                                JArray TD = (JArray)Message["data"];
                                if (TD.Any())
                                {
                                    try
                                    {
                                        /*
                                        JToken tok = TD.Children().LastOrDefault();
                                        Console.WriteLine("Last Token:" + tok.ToString());
                                        if (tok["walletBalance"]!=null)
                                        {
                                            Console.WriteLine("Has Wallet balance");
                                        }
                                        */
                                        JToken token = TD.Children().LastOrDefault()["walletBalance"];
                                        if (token != null)
                                        {
                                            Balance = ((decimal)token / 100000000);
                                            UpdateBalanceAndTime();
                                        }
                                        /*
                                        else
                                        {
                                            Console.WriteLine("Unable to get wallet balance? Token is null");
                                        }
                                        */
                                    }
                                    catch (Exception ex)
                                    {

                                    }
                                }
                            }
                        }
                        else if ((string)Message["table"] == "execution")
                        {
                            //Console.WriteLine("User Websocket execution");
                            if (Message.ContainsKey("data"))
                            {
                                //Console.WriteLine("Execution Data:" + Message["data"]);
                            }
                        }
                        else if ((string)Message["table"] == "order")
                        {
                            //Console.WriteLine("User Websocket order");
                            if (Message.ContainsKey("data"))
                            {
                                //Console.WriteLine("Order Data:" + Message["data"]);
                            }
                        }
                    }
                    else if (Message.ContainsKey("info") && Message.ContainsKey("docs"))
                    {
                        string WebSocketInfo = "User Websocket Info: " + Message["info"].ToString() + " " + Message["docs"].ToString();
                        UpdateWebSocketInfo(WebSocketInfo);
                    }
                }
                catch (Exception ex)
                {
                    //MessageBox.Show(ex.Message);
                }
            };
            ws_user.OnError += (sender, e) =>
            {
                Console.WriteLine("User Websocket on Error");
            };
            ws_user.OnClose += UserWebSocketOnClose;
            ws_user.OnOpen += (sender, e) =>
            {
                Console.WriteLine("User websocket on open");
                string APIExpires = bitmex.GetExpiresArg();
                string Signature = bitmex.GetWebSocketSignatureString(APISecret, APIExpires);
                ws_user.Send("{\"op\": \"authKeyExpires\", \"args\": [\"" + APIKey + "\", " + APIExpires + ", \"" + Signature + "\"]}");
                InitializeUserWS(FirstLoad);
            };
            ws_user.Connect();
            /*
            // Authenticate the API
            string APIExpires = bitmex.GetExpiresArg();
            string Signature = bitmex.GetWebSocketSignatureString(APISecret, APIExpires);
            ws_general.Send("{\"op\": \"authKeyExpires\", \"args\": [\"" + APIKey + "\", " + APIExpires + ", \"" + Signature + "\"]}");
            ws_user.Send("{\"op\": \"authKeyExpires\", \"args\": [\"" + APIKey + "\", " + APIExpires + ", \"" + Signature + "\"]}");
            */
            //// Chat Connect
            //ws.Send("{\"op\": \"subscribe\", \"args\": [\"chat\"]}");
            FirstLoad = false;

        }

        private void InitializePostSymbolInfoSettings()
        {
            nudStopTrailingLimitOffset.Value = Properties.Settings.Default.TrailingStopLimitOffset;
            nudStopTrailingTrail.Value = Properties.Settings.Default.TrailingStopTrail;
        }

        private void UpdateWebSocketInfo(string WebSocketInfo)
        {
            lblSettingsWebsocketInfo.Invoke(new Action(() => lblSettingsWebsocketInfo.Text = WebSocketInfo));
        }

        private void UpdatePositionInfo()
        {
            if (SymbolPosition.CurrentQty != 0)
            {
                txtPositionSize.Text = SymbolPosition.CurrentQty.ToString();
                txtPositionEntryPrice.Text = SymbolPosition.AvgEntryPrice.ToString();
                txtPositionMarkPrice.Text = SymbolPosition.MarkPrice.ToString();
                txtPositionLiquidation.Text = SymbolPosition.LiquidationPrice.ToString();
                txtPositionMargin.Text = SymbolPosition.Leverage.ToString();
                txtPositionUnrealizedPnL.Text = SymbolPosition.UsefulUnrealisedPnl.ToString();
                txtPositionUnrealizedPnLPercent.Text = SymbolPosition.UnrealisedPnlPcnt.ToString() + "%";
                if (nudPositionLimitPrice.Value == 0m) // Only updates when default value is present
                {
                    nudPositionLimitPrice.Value = Convert.ToDecimal(((int)Math.Floor((double)SymbolPosition.MarkPrice)).ToString() + ".0");
                }

            }
            else
            {
                txtPositionSize.Text = "0";
                txtPositionEntryPrice.Text = "0";
                txtPositionMarkPrice.Text = "0";
                txtPositionLiquidation.Text = "0";
                txtPositionMargin.Text = "0";
                txtPositionUnrealizedPnL.Text = "0";
                txtPositionUnrealizedPnLPercent.Text = "0";
            }
        }

        private void IntializeGeneralWS(bool FirstLoad = false)
        {
            L2Initialized = false;
            if (!FirstLoad)
            {
                // Unsubscribe from old orderbook
#if !USE_L2
                ws_general.Send("{\"op\": \"unsubscribe\", \"args\": [\"orderBook10:" + ActiveInstrument.Symbol + "\"]}");
#endif
                ws_general.Send("{\"op\": \"unsubscribe\", \"args\": [\"orderBookL2:" + ActiveInstrument.Symbol + "\"]}");
                ws_general.Send("{\"op\": \"unsubscribe\", \"args\": [\"trade:" + ActiveInstrument.Symbol + "\"]}");
                OrderBookTopAsks = new List<OrderBook>();
                OrderBookTopBids = new List<OrderBook>();
            }
            // Subscribe to new orderbook
#if !USE_L2
            ws_general.Send("{\"op\": \"subscribe\", \"args\": [\"orderBook10:" + ActiveInstrument.Symbol + "\"]}");
#endif
            ws_general.Send("{\"op\": \"subscribe\", \"args\": [\"orderBookL2:" + ActiveInstrument.Symbol + "\"]}");
            // Only subscribing to this symbol trade feed now, was too much at once before with them all.
            ws_general.Send("{\"op\": \"subscribe\", \"args\": [\"trade:" + ActiveInstrument.Symbol + "\"]}");
            UpdateFormsForTickSize(ActiveInstrument.TickSize, ActiveInstrument.DecimalPlacesInTickSize);
            Console.WriteLine("ReInitialized General websocket");
            ActiveInstrumentIndex = InstrumentIndex(ActiveInstrument.Symbol);
        }

        private void InitializeUserWS(bool FirstLoad = false)
        {
            if (!FirstLoad)
            {
                // Unsubscribe from old instrument position
                ws_user.Send("{\"op\": \"unsubscribe\", \"args\": [\"position:" + ActiveInstrument.Symbol + "\"]}");
            }
            // Subscribe to position for new symbol
            ws_user.Send("{\"op\": \"subscribe\", \"args\": [\"order\",\"wallet\",\"execution\"]}");
            if (FirstLoad)
            {
                ws_user.Send("{\"op\": \"subscribe\", \"args\": [\"order\",\"wallet\",\"execution\"]}");
            }
            Console.WriteLine("ReInitialized User websocket");
        }
        private void InitializeSymbolSpecificData(bool FirstLoad = false)
        {

            if (ActiveInstrument.Symbol == null)
            {
                return;
            }
            if (ws_general == null)
                return;
            if (ws_user == null)
                return;

            L2Initialized = false;

            if (!FirstLoad)
            {
                if (ActiveInstrument.Symbol!=null)
                {
                    // Unsubscribe from old orderbook
#if !USE_L2
                    ws_general.Send("{\"op\": \"unsubscribe\", \"args\": [\"orderBook10:" + ActiveInstrument.Symbol + "\"]}");
#endif
                    ws_general.Send("{\"op\": \"unsubscribe\", \"args\": [\"orderBookL2:" + ActiveInstrument.Symbol + "\"]}");
                    ws_general.Send("{\"op\": \"unsubscribe\", \"args\": [\"trade:" + ActiveInstrument.Symbol + "\"]}");
                    OrderBookTopAsks = new List<OrderBook>();
                    OrderBookTopBids = new List<OrderBook>();

                    // Unsubscribe from old instrument position
                    ws_user.Send("{\"op\": \"unsubscribe\", \"args\": [\"position:" + ActiveInstrument.Symbol + "\"]}");
                }

                ActiveInstrument = bitmex.GetInstrument(((Instrument)ddlSymbol.SelectedItem).Symbol)[0];
                ActiveInstrumentIndex = InstrumentIndex(ActiveInstrument.Symbol);
            }

            // Subscribe to new orderbook
#if !USE_L2
            ws_general.Send("{\"op\": \"subscribe\", \"args\": [\"orderBook10:" + ActiveInstrument.Symbol + "\"]}");
#endif
            ws_general.Send("{\"op\": \"subscribe\", \"args\": [\"orderBookL2:" + ActiveInstrument.Symbol + "\"]}");
            // Only subscribing to this symbol trade feed now, was too much at once before with them all.
            ws_general.Send("{\"op\": \"subscribe\", \"args\": [\"trade:" + ActiveInstrument.Symbol + "\"]}");
            // Subscribe to position for new symbol
            ws_user.Send("{\"op\": \"subscribe\", \"args\": [\"order\",\"wallet\",\"execution\"]}");
            if (FirstLoad)
            {
                ws_user.Send("{\"op\": \"subscribe\", \"args\": [\"order\",\"wallet\",\"execution\"]}");
            }

            UpdateFormsForTickSize(ActiveInstrument.TickSize, ActiveInstrument.DecimalPlacesInTickSize);

        }

        private void InitializeDropdownsAndSettings()
        {
            // Network/API Settings
            RealNetwork = (Properties.Settings.Default.Network == "Real"); // Set the bool = true if the setting is real network, false if test
            if (RealNetwork)
            {
                APIKey = Properties.Settings.Default.APIKey;
                APISecret = Properties.Settings.Default.APISecret;
            }
            else
            {
                APIKey = Properties.Settings.Default.TestAPIKey;
                APISecret = Properties.Settings.Default.TestAPISecret;
            }


            ddlCandleTimes.SelectedIndex = 0;

            // Spread Settings
            nudSpreadBuyOrderCount.Value = Properties.Settings.Default.SpreadBuyOrders;
            nudSpreadSellOrderCount.Value = Properties.Settings.Default.SpreadSellOrders;
            nudSpreadBuyValueApart.Value = Properties.Settings.Default.SpreadBuyValueApart;
            nudSpreadSellValueApart.Value = Properties.Settings.Default.SpreadSellValueApart;
            nudSpreadBuyContractsEach.Value = Properties.Settings.Default.SpreadBuyContractsEach;
            nudSpreadSellContractsEach.Value = Properties.Settings.Default.SpreadSellContractsEach;
            chkSpreadBuyReduceOnly.Checked = Properties.Settings.Default.SpreadBuyReduceOnly;
            chkSpreadSellReduceOnly.Checked = Properties.Settings.Default.SpreadSellReduceOnly;
            chkSpreadyBuyPostOnly.Checked = Properties.Settings.Default.SpreadBuyPostOnly;
            chkSpreadSellPostOnly.Checked = Properties.Settings.Default.SpreadSellPostOnly;
            chkSpreadBuyExecute.Checked = Properties.Settings.Default.SpreadBuyExecute;
            chkSpreadSellExecute.Checked = Properties.Settings.Default.SpreadSellExecute;
            chkSpreadCancelWhileOrdering.Checked = Properties.Settings.Default.SpreadCancelBeforeOrdering;


            // DCA Settings
            nudDCAContracts.Value = Properties.Settings.Default.DCAContracts;
            nudDCAHours.Value = Properties.Settings.Default.DCAHours;
            nudDCAMinutes.Value = Properties.Settings.Default.DCAMinutes;
            nudDCASeconds.Value = Properties.Settings.Default.DCASeconds;
            nudDCATimes.Value = Properties.Settings.Default.DCATimes;
            chkDCAReduceOnly.Checked = Properties.Settings.Default.DCAReduceOnly;

            // Setting Tab Settings
            chkSettingOverloadRetry.Checked = Properties.Settings.Default.OverloadRetry;
            nudSettingsOverloadRetryAttempts.Value = Properties.Settings.Default.OverloadRetryAttempts;
            nudSettingsRetryWaitTime.Value = Properties.Settings.Default.RetryAttemptWaitTime;

            // Manual Ordering Settings
            chkManualMarketBuyReduceOnly.Checked = Properties.Settings.Default.ManualMarketReduceOnly;
            //nudManualMarketBuyContracts.Value = Properties.Settings.Default.ManualMarketContracts; // Moved this to Post API settings, because intrument data is required.
            nudManualLimitContracts.Value = Properties.Settings.Default.ManualLimitContracts;
            nudManualLimitPrice.Value = Properties.Settings.Default.ManualLimitPrice;
            chkManualLimitReduceOnly.Checked = Properties.Settings.Default.ManualLimitReduceOnly;
            chkManualLimitPostOnly.Checked = Properties.Settings.Default.ManualLimitPostOnly;
            chkManualLimitCancelWhileOrdering.Checked = Properties.Settings.Default.ManualLimitCancelOpenOrders;
            chkManualLimitHiddenOrder.Checked = Properties.Settings.Default.ManualLimitHiddenOrder;
            nudManualLimitPercentModifier1.Value = Properties.Settings.Default.ManualLimitPercentModifier1;
            nudManualLimitPercentModifier2.Value = Properties.Settings.Default.ManualLimitPercentModifier2;
            nudManualLimitPercentModifier3.Value = Properties.Settings.Default.ManualLimitPercentModifier3;
            nudManualLimitPercentModifier4.Value = Properties.Settings.Default.ManualLimitPercentModifier4;
            chkManualLimitPercentModifierUseCurrentPrice.Checked = Properties.Settings.Default.ManualLimitPercentModifierUseCurrentPrice;

            // Limit Now
            nudLimitNowBuyContracts.Value = Properties.Settings.Default.LimitNowBuyContracts;
            nudLimitNowBuyTicksFromCenter.Value = Properties.Settings.Default.LimitNowBuyTicksFromCenter;
            nudLimitNowBuyDelay.Value = Properties.Settings.Default.LimitNowBuyDelay;
            chkLimitNowBuyContinue.Checked = Properties.Settings.Default.LimitNowBuyContinue;
            tmrLimitNowBuy.Interval = Properties.Settings.Default.LimitNowBuyDelay;
            tmrLimitNowSell.Interval = Properties.Settings.Default.LimitNowBuyDelay;
            nudLimitNowSellContracts.Value = Properties.Settings.Default.LimitNowSellContracts;
            nudLimitNowSellTicksFromCenter.Value = Properties.Settings.Default.LimitNowSellTicksFromCenter;
            nudLimitNowSellDelay.Value = Properties.Settings.Default.LimitNowSellDelay;
            chkLimitNowSellContinue.Checked = Properties.Settings.Default.LimitNowSellContinue;
            ddlLimitNowBuyMethod.SelectedItem = Properties.Settings.Default.LimitNowBuyMethod;
            ddlLimitNowSellMethod.SelectedItem = Properties.Settings.Default.LimitNowSellMethod;
            chkLimitNowBuyReduceOnly.Checked = Properties.Settings.Default.LimitNowBuyReduceOnly;
            chkLimitNowSellReduceOnly.Checked = Properties.Settings.Default.LimitNowSellReduceOnly;
            chkLimitNowStopLossBuy.Checked = Properties.Settings.Default.LimitNowStopLossBuy;
            chkLimitNowStopLossSell.Checked = Properties.Settings.Default.LimitNowStopLossSell;
            chkLimitNowOrderBookDetectionBuy.Checked = Properties.Settings.Default.LimitNowOrderBookBuy;
            chkLimitNowOrderBookDetectionSell.Checked = Properties.Settings.Default.LimitNowOrderBookSell;
            nudLimitNowStopLossBuyDelta.Value = Properties.Settings.Default.LimitNowStopLossBuyDelta;
            nudLimitNowStopLossSellDelta.Value = Properties.Settings.Default.LimitNowStopLossSellDelta;
            nudLimitNowBuyLevel.Value = Properties.Settings.Default.LimitNowBuyLevel;
            nudLimitNowSellLevel.Value = Properties.Settings.Default.LimitNowSellLevel;

            LimitNowSellTicksFromCenter = Properties.Settings.Default.LimitNowSellTicksFromCenter;
            LimitNowBuyTicksFromCenter = Properties.Settings.Default.LimitNowBuyTicksFromCenter;

            // Trailing Stop
            nudStopTrailingContracts.Value = Properties.Settings.Default.TrailingStopContracts;
            ddlStopTrailingMethod.SelectedItem = Properties.Settings.Default.TrailingStopMethod;
            chkStopTrailingCloseInFull.Checked = Properties.Settings.Default.TrailingStopCloseInFull;


            // Tab
            this.TabControl.SelectedIndex = Properties.Settings.Default.TabSelection;

            // Update other client items...
            UpdateNetworkAndVersion();
        }

        private void UpdateNetworkAndVersion()
        {
            if (RealNetwork)
            {
                lblNetworkAndVersion.Text = "Real" + " v" + Version;
            }
            else
            {
                lblNetworkAndVersion.Text = "Test" + " v" + Version;
            }
        }

        private void InitializePostAPIDropdownsAndSettings()
        {
            // Manual Ordering Settings
            nudManualMarketBuyContracts.Value = Properties.Settings.Default.ManualMarketContracts;
        }

        private void UpdateAPICallsRemaining(object sender, EventArgs e)
        {
            //Console.WriteLine("Update APICalls");
            txtMaxCall.Invoke((MethodInvoker)(()=>
            {
                txtMaxCall.Text = bitmex.MaxCallsLimit.ToString() ;
                txtRemainingCall.Text = bitmex.CallsRemaining.ToString();
            }));
        }

        private void InitializeAPI()
        {
            try
            {
                bitmex = new BitMEXApi(APIKey, APISecret, RealNetwork);
                bitmex.UpdateApiRemainingHandler += UpdateAPICallsRemaining;
                // Show users what network they are on.
                UpdateNetworkAndVersion();

                // Start our HeartBeat
                //Heartbeat.Start();
            }
            catch (Exception ex)
            {
            }
        }

        private void InitializeSymbolInformation()
        {
            AllInstruments = bitmex.GetAllInstruments();
            ActiveInstruments = bitmex.GetActiveInstruments().OrderByDescending(a => a.Volume24H).ToList();
            // Assemble our price dictionary
            foreach (Instrument i in ActiveInstruments)
            {
                Prices.Add(i.Symbol, 0); // just setting up the item, 0 is fine here.
            }
        }

        private void InitializeDependentSymbolInformation()
        {
            ddlSymbol.DataSource = ActiveInstruments;
            ddlSymbol.DisplayMember = "Symbol";
            ddlSymbol.SelectedIndex = 0;
            ActiveInstrument = ActiveInstruments[0];
            ActiveInstrumentIndex = InstrumentIndex(ActiveInstrument.Symbol);

            InitializeSymbolSpecificData(true);
        }
#endregion

#region General Tools

        private void SaveSettings()
        {
            Properties.Settings.Default.Save();
        }

        private void pbxYouTubeSubscribe_Click(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("https://www.youtube.com/BigBits?sub_confirmation=1");
        }

        private void lblDonate_Click(object sender, EventArgs e)
        {
            TabControl.SelectTab("tabDonate");
        }

        private void UpdatePrice()
        {
            nudCurrentPrice.Value = Prices[ActiveInstrument.Symbol];
        }

        private void UpdatePriceThread(string direction)
        {
            nudCurrentPrice.Invoke((MethodInvoker)(()=>
            {
                nudCurrentPrice.Value = Prices[ActiveInstrument.Symbol];
                /*
                //Console.WriteLine("Updown num:" + nudCurrentPrice.Controls.Count);
                //Console.WriteLine("Control 0 :" + nudCurrentPrice.Controls[0]);
                Console.WriteLine("Control 1 :" + nudCurrentPrice.Controls[1]);
                nudCurrentPrice.Controls[1].ForeColor = System.Drawing.Color.Yellow;
                Console.WriteLine("Edit:"+nudCurrentPrice.Controls[1].Controls.Count);
                FieldInfo editProp = nudCurrentPrice.GetType().GetField("upDownEdit", BindingFlags.Instance | BindingFlags.NonPublic);
                TextBox edit = (TextBox)editProp.GetValue(nudCurrentPrice);
                edit.ForeColor = System.Drawing.Color.Blue;
                //nudCurrentPrice.Controls[1].
                */
            }));
        }

        private void tmrClientUpdates_Tick(object sender, EventArgs e)
        {
            UpdatePrice();
            UpdateManualMarketBuyButtons();  // Update our buy buttons on manual market buys
            UpdatePositionInfo();
            //TriggerAlerts();
        }
#endregion

#region Symbol And Time Frame Tools
        private void ddlSymbol_SelectedIndexChanged(object sender, EventArgs e)
        {
            InitializeSymbolSpecificData();
        }

        private void UpdateFormsForTickSize(decimal TickSize, int Decimals)
        {
            Log("Current Tick size:" + TickSize + ":" + Decimals);
            nudPositionLimitPrice.Invoke((MethodInvoker)(()=>
                {
                    nudPositionLimitPrice.DecimalPlaces = Decimals;
                    nudPositionLimitPrice.Increment = TickSize;

                    nudSpreadBuyValueApart.DecimalPlaces = Decimals;
                    nudSpreadBuyValueApart.Increment = TickSize;
                    nudSpreadBuyValueApart.Value = Math.Round(nudSpreadBuyValueApart.Value, Decimals);

                    nudSpreadSellValueApart.DecimalPlaces = Decimals;
                    nudSpreadSellValueApart.Increment = TickSize;
                    nudSpreadSellValueApart.Value = Math.Round(nudSpreadSellValueApart.Value, Decimals);

                    nudCurrentPrice.DecimalPlaces = Decimals;
                    nudCurrentPrice.Increment = TickSize;
                    nudCurrentPrice.Controls[0].Enabled = false;
                    nudCurrentPrice.Value = Math.Round(nudCurrentPrice.Value, Decimals);

                    nudManualLimitPrice.DecimalPlaces = Decimals;
                    nudManualLimitPrice.Increment = TickSize;

                    nudStopTrailingTrail.DecimalPlaces = Decimals;
                    nudStopTrailingTrail.Increment = TickSize;
                    nudStopTrailingTrail.Value = Math.Round(nudStopTrailingTrail.Value, Decimals);

                    nudStopTrailingLimitOffset.DecimalPlaces = Decimals;
                    nudStopTrailingLimitOffset.Increment = TickSize;
                    nudStopTrailingLimitOffset.Value = Math.Round(nudStopTrailingLimitOffset.Value, Decimals);
                }));
        }

        private void ddlCandleTimes_SelectedIndexChanged(object sender, EventArgs e)
        {
            Timeframe = ddlCandleTimes.SelectedItem.ToString();
        }

        private void Heartbeat_Tick(object sender, EventArgs e)
        {
            if (DateTime.UtcNow.Second == 0)
            {
                //Update our balance each minute
                //UpdateBalanceAndTime();
            }

            // Update the time every second.
            UpdateBalanceAndTime();

            if (((TimeSpan)(DateTime.UtcNow - GeneralWebScocketLastMessage)).TotalSeconds > heartbeatcheck)
            {
                //Console.WriteLine("general websocket send ping");
                //ws_general.Ping();
                ws_general.Send("ping");
            }

            if (((TimeSpan)(DateTime.UtcNow - UserWebScocketLastMessage)).TotalSeconds > heartbeatcheck)
            {
                //Console.WriteLine("User websocket send ping");
                //ws_user.Ping();
                ws_user.Send("ping");
            }

        }

        private void UpdateBalanceAndTime()
        {
            int HoursInFuture = 0;
            try
            {
                string USDValue = (Prices["XBTUSD"] * Balance).ToString("C", new CultureInfo("en-US"));
                lblBalanceAndTime.Invoke(
                    new Action(() => lblBalanceAndTime.Text = "Balance: " + Math.Round(Balance, 8).ToString() + " | " + USDValue + "     " +
#if USE_LOCALTIME
                    DateTime.Now.ToShortDateString() + " " + DateTime.Now.AddHours(HoursInFuture).ToLongTimeString() + "local"
#else
                    DateTime.UtcNow.ToShortDateString() + " " + DateTime.UtcNow.AddHours(HoursInFuture).ToLongTimeString() + "Utc"
#endif
                    ));

            }
            catch (Exception ex)
            {
                lblBalanceAndTime.Invoke(
                    new Action(() => lblBalanceAndTime.Text = "Balance: Error     " +
#if USE_LOCALTIME
                    DateTime.Now.ToShortDateString() + " " + DateTime.Now.AddHours(HoursInFuture).ToLongTimeString() + "local"
#else
                    DateTime.UtcNow.ToShortDateString() + " " + DateTime.UtcNow.AddHours(HoursInFuture).ToLongTimeString() + "Utc"
#endif
                    ));
            }
        }
#endregion

#region DCA
        private void UpdateDCASummary()
        {
            DCASelectedSymbol = ActiveInstrument.Symbol;
            DCAContractsPer = Convert.ToInt32(nudDCAContracts.Value);
            DCAHours = Convert.ToInt32(nudDCAHours.Value);
            DCAMinutes = Convert.ToInt32(nudDCAMinutes.Value);
            DCASeconds = Convert.ToInt32(nudDCASeconds.Value);
            DCATimes = Convert.ToInt32(nudDCATimes.Value);

            DateTime Start = DateTime.UtcNow;
            DateTime End = new DateTime();
            if (chkDCAExecuteImmediately.Checked)
            {
                End = DateTime.UtcNow.AddHours(DCAHours * (DCATimes - 1)).AddMinutes(DCAMinutes * (DCATimes - 1)).AddSeconds(DCASeconds * (DCATimes - 1));
            }
            else
            {
                End = DateTime.UtcNow.AddHours(DCAHours * DCATimes).AddMinutes(DCAMinutes * DCATimes).AddSeconds(DCASeconds * DCATimes);
            }
            TimeSpan Duration = End - Start;

            if (Duration.TotalMinutes < 1.0)
            {
                lblDCASummary.Text = (DCAContractsPer * DCATimes).ToString() + " Contracts over " + DCATimes.ToString() + " orders during a total of " + Duration.Seconds.ToString() + " seconds.";
            }
            else if (Duration.TotalHours < 1.0)
            {
                lblDCASummary.Text = (DCAContractsPer * DCATimes).ToString() + " Contracts over " + DCATimes.ToString() + " orders during a total of " + Duration.Minutes.ToString() + " minutes " + Duration.Seconds.ToString() + " seconds.";
            }
            else
            {
                lblDCASummary.Text = (DCAContractsPer * DCATimes).ToString() + " Contracts over " + DCATimes.ToString() + " orders during a total of " + ((int)Math.Floor(Duration.TotalHours)).ToString() + " hours " + Duration.Minutes.ToString() + " minutes " + Duration.Seconds.ToString() + " seconds.";
            }



        }

        private void nudDCAContracts_ValueChanged(object sender, EventArgs e)
        {
            DCAContractsPer = Convert.ToInt32(nudDCAContracts.Value);
            Properties.Settings.Default.DCAContracts = DCAContractsPer;
            SaveSettings();
            UpdateDCASummary();
        }

        private void nudDCAHours_ValueChanged(object sender, EventArgs e)
        {
            DCAHours = Convert.ToInt32(nudDCAHours.Value);
            Properties.Settings.Default.DCAHours = DCAHours;
            SaveSettings();
            UpdateDCASummary();
        }

        private void nudDCAMinutes_ValueChanged(object sender, EventArgs e)
        {
            DCAMinutes = Convert.ToInt32(nudDCAMinutes.Value);
            Properties.Settings.Default.DCAMinutes = DCAMinutes;
            SaveSettings();
            UpdateDCASummary();
        }

        private void nudDCASeconds_ValueChanged(object sender, EventArgs e)
        {
            DCASeconds = Convert.ToInt32(nudDCASeconds.Value);
            Properties.Settings.Default.DCASeconds = DCASeconds;
            SaveSettings();
            UpdateDCASummary();
        }

        private void nudDCATimes_ValueChanged(object sender, EventArgs e)
        {
            DCATimes = Convert.ToInt32(nudDCATimes.Value);
            Properties.Settings.Default.DCATimes = DCATimes;
            SaveSettings();
            UpdateDCASummary();
        }

        private void chkDCAReduceOnly_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.DCAReduceOnly = chkDCAReduceOnly.Checked;
            SaveSettings();
        }

        private void chkDCAExecuteImmediately_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.DCAExecuteImmediately = chkDCAExecuteImmediately.Checked;
            SaveSettings();
            UpdateDCASummary();
        }

        private void btnDCABuy_Click(object sender, EventArgs e)
        {
            UpdateDCASummary(); // Makes sure our variables are current.

            DCASide = "Buy";

            tmrDCA.Interval = (DCASeconds * 1000) + (DCAMinutes * 60 * 1000) + (DCAHours * 60 * 60 * 1000); // Must multiply by 1000, because timers operate in milliseconds.
            tmrDCA.Start(); // Start the timer.
            pgbDCA.Value = 0;
            LockDCA();

            // Execute first order immediately
            if (chkDCAExecuteImmediately.Checked)
            {
                DCAAction();
            }
        }

        private void btnDCASell_Click(object sender, EventArgs e)
        {
            UpdateDCASummary(); // Makes sure our variables are current.

            DCASide = "Sell";

            tmrDCA.Interval = (DCASeconds * 1000) + (DCAMinutes * 60 * 1000) + (DCAHours * 60 * 60 * 1000); // Must multiply by 1000, because timers operate in milliseconds.
            tmrDCA.Start(); // Start the timer.
            pgbDCA.Value = 0;
            LockDCA();

            // Execute first order immediately
            if (chkDCAExecuteImmediately.Checked)
            {
                DCAAction();
            }
        }

        private void btnDCAStop_Click(object sender, EventArgs e)
        {
            DCACounter = 0;
            pgbDCA.Value = 0;
            tmrDCA.Stop();
            LockDCA(false);
        }

        private void tmrDCA_Tick(object sender, EventArgs e)
        {
            DCAAction();
        }

        private void DCAAction()
        {
            DCACounter++;
            bitmex.MarketOrder(DCASelectedSymbol, DCASide, DCAContractsPer, chkDCAReduceOnly.Checked);

            decimal Percent = (DCACounter / DCATimes) * 100;
            pgbDCA.Value = Convert.ToInt32(Math.Round(Percent));

            if (DCACounter == DCATimes)
            {
                DCACounter = 0;
                tmrDCA.Stop();
                pgbDCA.Value = 0;
                LockDCA(false);

            }
        }

        private void LockDCA(bool Lock = true)
        {
            nudDCAContracts.Enabled = !Lock;
            nudDCAHours.Enabled = !Lock;
            nudDCAMinutes.Enabled = !Lock;
            nudDCASeconds.Enabled = !Lock;
            nudDCATimes.Enabled = !Lock;
            pgbDCA.Visible = Lock;
            btnDCABuy.Visible = !Lock;
            btnDCASell.Visible = !Lock;
            btnDCAStop.Visible = Lock;
            chkDCAReduceOnly.Enabled = !Lock;
            chkDCAExecuteImmediately.Enabled = !Lock;
        }

#endregion

#region Position Manager
        private void btnPositionMarketClose_Click(object sender, EventArgs e)
        {
            MarketClosePosition();
        }

        private void MarketClosePosition()
        {
            int Size = (int)SymbolPosition.CurrentQty;
            string Side = "Buy";

            if (Size < 0) // We are short
            {
                Side = "Buy";
                Size = (int)Math.Abs((decimal)Size); // Makes sure size is positive number
            }
            else if (Size > 0)
            {
                Side = "Sell";
                Size = (int)Math.Abs((decimal)Size); // Makes sure size is positive number
            }
            if (Size != 0)
            {
                bitmex.MarketOrder(ActiveInstrument.Symbol, Side, Size, true);
            }
        }

        private void btnPositionLimitClose_Click(object sender, EventArgs e)
        {
            try
            {
                decimal Price = nudPositionLimitPrice.Value;

                // We have entered a valid price
                int Size = Convert.ToInt32(txtPositionSize.Text);
                string Side = "Buy";

                if (Size < 0) // We are short
                {
                    Side = "Buy";
                    Size = (int)Math.Abs((decimal)Size); // Makes sure size is positive number
                }
                else if (Size > 0)
                {
                    Side = "Sell";
                    Size = (int)Math.Abs((decimal)Size); // Makes sure size is positive number
                }
                bitmex.LimitOrder(ActiveInstrument.Symbol, Side, Size, Price, true);
            }
            catch (Exception ex)
            {

            }

        }

        private void btnPositionMargin_Click(object sender, EventArgs e)
        {
            bitmex.ChangeMargin(ActiveInstrument.Symbol, nudPositionMargin.Value);
        }
#endregion

#region Settings Tab
        private void chkSettingOverloadRetry_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.OverloadRetry = chkSettingOverloadRetry.Checked;
            SaveSettings();
        }

        private void nudSettingsOverloadRetryAttempts_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.OverloadRetryAttempts = (int)nudSettingsOverloadRetryAttempts.Value;
            SaveSettings();
        }

        private void nudSettingsRetryWaitTime_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.RetryAttemptWaitTime = (int)nudSettingsRetryWaitTime.Value;
            SaveSettings();
        }
#endregion

#region Spread Orders

        private void nudSpreadBuyOrderCount_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.SpreadBuyOrders = (int)nudSpreadBuyOrderCount.Value;
            SaveSettings();
        }

        private void nudSpreadSellOrderCount_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.SpreadSellOrders = (int)nudSpreadSellOrderCount.Value;
            SaveSettings();
        }

        private void nudSpreadBuyValueApart_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.SpreadBuyValueApart = nudSpreadBuyValueApart.Value;
            SaveSettings();
        }

        private void nudSpreadSellValueApart_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.SpreadSellValueApart = nudSpreadSellValueApart.Value;
            SaveSettings();
        }

        private void nudSpreadBuyContractsEach_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.SpreadBuyContractsEach = (int)nudSpreadBuyContractsEach.Value;
            SaveSettings();
        }

        private void nudSpreadSellContractsEach_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.SpreadSellContractsEach = (int)nudSpreadSellContractsEach.Value;
            SaveSettings();
        }

        private void chkSpreadBuyReduceOnly_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.SpreadBuyReduceOnly = chkSpreadBuyReduceOnly.Checked;
            SaveSettings();
        }

        private void chkSpreadSellReduceOnly_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.SpreadSellReduceOnly = chkSpreadSellReduceOnly.Checked;
            SaveSettings();
        }

        private void chkSpreadyBuyPostOnly_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.SpreadBuyPostOnly = chkSpreadyBuyPostOnly.Checked;
            SaveSettings();
        }

        private void chkSpreadSellPostOnly_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.SpreadSellPostOnly = chkSpreadSellPostOnly.Checked;
            SaveSettings();
        }

        private void chkSpreadBuyExecute_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.SpreadBuyExecute = chkSpreadBuyExecute.Checked;
            SaveSettings();
        }

        private void chkSpreadSellExecute_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.SpreadSellExecute = chkSpreadSellExecute.Checked;
            SaveSettings();
        }

        private void chkSpreadCancelWhileOrdering_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.SpreadCancelBeforeOrdering = chkSpreadCancelWhileOrdering.Checked;
            SaveSettings();
        }

        private void btnSpreadCloseAllOpenOrders_Click(object sender, EventArgs e)
        {
            bitmex.CancelAllOpenOrders(ActiveInstrument.Symbol);
        }

        private void btnSpreadPlaceOrders_Click(object sender, EventArgs e)
        {
            // do our logic for creating a bulk order to submit
            List<Order> BulkOrders = new List<Order>();

            // Step 1, see if we need to cancel all open orders and do it if so
            if (chkSpreadCancelWhileOrdering.Checked)
            {
                // Cancel all open orders
                bitmex.CancelAllOpenOrders(ActiveInstrument.Symbol);
            }

            // Step 2, check to see if we even need to bother building buy or sell orders
            // Step 3, if we do, respectively create each individual order necessary based on settings logic
            decimal CurrentPrice = bitmex.GetCurrentPrice(ActiveInstrument.Symbol);

            if (chkSpreadBuyExecute.Checked)
            {
                // build our buy orders
                for (int i = 1; i <= (int)nudSpreadBuyOrderCount.Value; i++)
                {
                    Order o = new Order();
                    o.Side = "Buy";
                    o.OrderQty = (int?)nudSpreadBuyContractsEach.Value;
                    o.Symbol = ActiveInstrument.Symbol;
                    o.Price = (decimal?)(CurrentPrice - (nudSpreadBuyValueApart.Value * i));
                    if (chkSpreadBuyReduceOnly.Checked && chkSpreadyBuyPostOnly.Checked)
                    {
                        o.ExecInst = "ReduceOnly,ParticipateDoNotInitiate";
                    }
                    else if (!chkSpreadBuyReduceOnly.Checked && chkSpreadyBuyPostOnly.Checked)
                    {
                        o.ExecInst = "ParticipateDoNotInitiate";
                    }
                    else if (chkSpreadBuyReduceOnly.Checked && !chkSpreadyBuyPostOnly.Checked)
                    {
                        o.ExecInst = "ReduceOnly";
                    }
                    BulkOrders.Add(o);
                }
            }
            if (chkSpreadSellExecute.Checked)
            {
                // build our sell orders
                for (int i = 1; i <= (int)nudSpreadSellOrderCount.Value; i++)
                {
                    Order o = new Order();
                    o.Side = "Sell";
                    o.OrderQty = (int?)nudSpreadSellContractsEach.Value;
                    o.Symbol = ActiveInstrument.Symbol;
                    o.Price = (decimal?)(CurrentPrice + (nudSpreadSellValueApart.Value * i));
                    if (chkSpreadSellReduceOnly.Checked && chkSpreadSellPostOnly.Checked)
                    {
                        o.ExecInst = "ReduceOnly,ParticipateDoNotInitiate";
                    }
                    else if (!chkSpreadSellReduceOnly.Checked && chkSpreadSellPostOnly.Checked)
                    {
                        o.ExecInst = "ParticipateDoNotInitiate";
                    }
                    else if (chkSpreadSellReduceOnly.Checked && !chkSpreadSellPostOnly.Checked)
                    {
                        o.ExecInst = "ReduceOnly";
                    }
                    BulkOrders.Add(o);
                }
            }

            // Step 4, call the bulk order submit button
            string BulkOrderString = BuildBulkOrder(BulkOrders);
            bitmex.BulkOrder(BulkOrderString);

        }

        private string BuildBulkOrder(List<Order> Orders, bool Amend = false)
        {
            StringBuilder str = new StringBuilder();

            str.Append("[");

            int i = 1;
            foreach (Order o in Orders)
            {
                if (i > 1)
                {
                    str.Append(", ");
                }
                str.Append("{");
                if (Amend == true)
                {
                    str.Append("\"orderID\": \"" + o.OrderId.ToString() + "\", ");
                }
                str.Append("\"orderQty\": " + o.OrderQty.ToString() + ", \"price\": " + o.Price.ToString() + ", \"side\": \"" + o.Side + "\", \"symbol\": \"" + o.Symbol + "\"");
                if (o.ExecInst != null)
                {
                    if (o.ExecInst.Trim() != "")
                    {
                        str.Append(", \"execInst\": \"" + o.ExecInst + "\"");
                    }
                }
                str.Append("}");
                i++;
            }

            str.Append("]");

            return str.ToString();
        }

#endregion

#region Export Candles
        private void ExportCandleData()
        {
            // First see if we have the file we want where we want it. To do that, we need to get the filepath to our app folder in my documents
            string path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); // We are working in My Documents.
            if (!Directory.Exists(path + "\\BitMEXAssistant"))
            {
                // If our Kizashi Logs folder doesn't exist, create it.
                Directory.CreateDirectory(path + "\\BitMEXAssistant");
            }

            // Optionally, you could loop through all symbols and timeframes to get all files at once here
            string ourfilepath = Path.Combine(path, "BitMEXAssistant", "Assistant" + ActiveInstrument.Symbol + Timeframe + "CandleHistory.csv");
            // Get candle info, and Account balance
            if (!File.Exists(ourfilepath))
            {
                // If our files doesn't exist, we'll creat it now with the stream writer
                using (StreamWriter write = new StreamWriter(ourfilepath))
                {
                    CsvWriter csw = new CsvWriter(write);

                    csw.WriteHeader<SimpleCandle>(); // writes the csv header for this class
                    csw.NextRecord();

                    // loop through all candles, add those items to the csv while we are getting 500 candles (full datasets)
                    List<SimpleCandle> Candles = GetSimpleCandles(ActiveInstrument.Symbol, Timeframe).Where(a => a.Trades > 0).ToList();
                    while (Candles.Count > 0)
                    {

                        csw.WriteRecords(Candles);

                        // Get candles with a start time of the last candle plus 1 min
                        switch (Timeframe)
                        {
                            case "1m":
                                Candles = GetSimpleCandles(ActiveInstrument.Symbol, Timeframe, Candles.OrderByDescending(a => a.TimeStamp).FirstOrDefault().TimeStamp.AddMinutes(1)).Where(a => a.Trades > 0).ToList();
                                break;
                            case "5m":
                                Candles = GetSimpleCandles(ActiveInstrument.Symbol, Timeframe, Candles.OrderByDescending(a => a.TimeStamp).FirstOrDefault().TimeStamp.AddMinutes(5)).Where(a => a.Trades > 0).ToList();
                                break;
                            case "1h":
                                Candles = GetSimpleCandles(ActiveInstrument.Symbol, Timeframe, Candles.OrderByDescending(a => a.TimeStamp).FirstOrDefault().TimeStamp.AddHours(1)).Where(a => a.Trades > 0).ToList();
                                break;
                            case "1d":
                                Candles = GetSimpleCandles(ActiveInstrument.Symbol, Timeframe, Candles.OrderByDescending(a => a.TimeStamp).FirstOrDefault().TimeStamp.AddDays(1)).Where(a => a.Trades > 0).ToList();
                                break;
                            default:
                                Candles = GetSimpleCandles(ActiveInstrument.Symbol, Timeframe, Candles.OrderByDescending(a => a.TimeStamp).FirstOrDefault().TimeStamp.AddMinutes(1)).Where(a => a.Trades > 0).ToList();
                                break;
                        }

                        // Lets sleep for a bit, 5 seconds, don't want to get rate limited
                        Thread.Sleep(2500);
                    }

                }
            }
            else
            {
                // our file exists, let read existing contents, add them back in, with the new candles.
                string ourtemppath = Path.Combine(path, "BitMEXAssistant", "Assistant" + ActiveInstrument.Symbol + Timeframe + "CandleHistory.csv");
                // Open our file, and append data to it.
                using (StreamReader reader = new StreamReader(ourfilepath))
                {
                    using (StreamWriter write = new StreamWriter(ourtemppath))
                    {
                        CsvWriter csw = new CsvWriter(write);
                        CsvReader csr = new CsvReader(reader);

                        // Recreate existing records, then add new ones.
                        List<SimpleCandle> records = csr.GetRecords<SimpleCandle>().ToList();

                        csw.WriteRecords(records);

                        // Now to any new since the most recent record
                        List<SimpleCandle> Candles = new List<SimpleCandle>();
                        // Get candles with a start time of the last candle plus 1 min
                        switch (Timeframe)
                        {
                            case "1m":
                                Candles = GetSimpleCandles(ActiveInstrument.Symbol, Timeframe, records.OrderByDescending(a => a.TimeStamp).FirstOrDefault().TimeStamp.AddMinutes(1)).Where(a => a.Trades > 0).ToList();
                                break;
                            case "5m":
                                Candles = GetSimpleCandles(ActiveInstrument.Symbol, Timeframe, records.OrderByDescending(a => a.TimeStamp).FirstOrDefault().TimeStamp.AddMinutes(5)).Where(a => a.Trades > 0).ToList();
                                break;
                            case "1h":
                                Candles = GetSimpleCandles(ActiveInstrument.Symbol, Timeframe, records.OrderByDescending(a => a.TimeStamp).FirstOrDefault().TimeStamp.AddHours(1)).Where(a => a.Trades > 0).ToList();
                                break;
                            case "1d":
                                Candles = GetSimpleCandles(ActiveInstrument.Symbol, Timeframe, records.OrderByDescending(a => a.TimeStamp).FirstOrDefault().TimeStamp.AddDays(1)).Where(a => a.Trades > 0).ToList();
                                break;
                            default:
                                Candles = GetSimpleCandles(ActiveInstrument.Symbol, Timeframe, records.OrderByDescending(a => a.TimeStamp).FirstOrDefault().TimeStamp.AddMinutes(1)).Where(a => a.Trades > 0).ToList();
                                break;
                        }

                        // loop through all candles, add those items to the csv while we are getting 500 candles (full datasets)

                        while (Candles.Count > 0)
                        {

                            csw.WriteRecords(Candles);

                            // Get candles with a start time of the last candle plus 1 min
                            switch (Timeframe)
                            {
                                case "1m":
                                    Candles = GetSimpleCandles(ActiveInstrument.Symbol, Timeframe, Candles.OrderByDescending(a => a.TimeStamp).FirstOrDefault().TimeStamp.AddMinutes(1)).Where(a => a.Trades > 0).ToList();
                                    break;
                                case "5m":
                                    Candles = GetSimpleCandles(ActiveInstrument.Symbol, Timeframe, Candles.OrderByDescending(a => a.TimeStamp).FirstOrDefault().TimeStamp.AddMinutes(5)).Where(a => a.Trades > 0).ToList();
                                    break;
                                case "1h":
                                    Candles = GetSimpleCandles(ActiveInstrument.Symbol, Timeframe, Candles.OrderByDescending(a => a.TimeStamp).FirstOrDefault().TimeStamp.AddHours(1)).Where(a => a.Trades > 0).ToList();
                                    break;
                                case "1d":
                                    Candles = GetSimpleCandles(ActiveInstrument.Symbol, Timeframe, Candles.OrderByDescending(a => a.TimeStamp).FirstOrDefault().TimeStamp.AddDays(1)).Where(a => a.Trades > 0).ToList();
                                    break;
                                default:
                                    Candles = GetSimpleCandles(ActiveInstrument.Symbol, Timeframe, Candles.OrderByDescending(a => a.TimeStamp).FirstOrDefault().TimeStamp.AddMinutes(1)).Where(a => a.Trades > 0).ToList();
                                    break;
                            }

                            // Lets sleep for a bit, 5 seconds, don't want to get rate limited
                            Thread.Sleep(2500);
                        }

                    }
                }

                File.Delete(ourfilepath);
                File.Copy(ourtemppath, ourfilepath);
                File.Delete(ourtemppath);
            }


        }

        private List<SimpleCandle> GetSimpleCandles(string Symbol, string Timeframe, DateTime Start = new DateTime())
        {
            List<SimpleCandle> Candles = new List<SimpleCandle>();
            if (Start != new DateTime())
            {
                Candles = bitmex.GetCandleHistory(Symbol, Timeframe, Start);
            }
            else
            {
                Candles = bitmex.GetCandleHistory(Symbol, Timeframe);
            }

            return Candles;
        }

        private void btnExportCandles_Click(object sender, EventArgs e)
        {
            ExportCandleData();
        }
#endregion

#region Manual Ordering

        private void UpdateManualMarketBuyButtons()
        {
            btnManualMarketBuy.Text = "Market Buy" + Environment.NewLine + ((int)nudManualMarketBuyContracts.Value).ToString() + " @" + nudCurrentPrice.Value.ToString("F" + ActiveInstrument.DecimalPlacesInTickSize.ToString());
            btnManualMarketSell.Text = "Market Sell" + Environment.NewLine + ((int)nudManualMarketBuyContracts.Value).ToString() + " @" + nudCurrentPrice.Value.ToString("F" + ActiveInstrument.DecimalPlacesInTickSize.ToString());
        }

        private void btnManualMarketBuy_Click(object sender, EventArgs e)
        {
            bitmex.MarketOrder(ActiveInstrument.Symbol, "Buy", (int)nudManualMarketBuyContracts.Value, chkManualMarketBuyReduceOnly.Checked);
        }

        private void btnManualMarketSell_Click(object sender, EventArgs e)
        {
            bitmex.MarketOrder(ActiveInstrument.Symbol, "Sell", (int)nudManualMarketBuyContracts.Value, chkManualMarketBuyReduceOnly.Checked);
        }

        private void nudManualMarketBuyContracts_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.ManualMarketContracts = (int)nudManualMarketBuyContracts.Value;
            SaveSettings();
            UpdateManualMarketBuyButtons();
        }

        private void chkManualMarketBuyReduceOnly_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.ManualMarketReduceOnly = chkManualMarketBuyReduceOnly.Checked;
            SaveSettings();
        }

        private void btnManualLimitSetCurrentPrice_Click(object sender, EventArgs e)
        {
            nudManualLimitPrice.Value = Prices[ActiveInstrument.Symbol];
        }

        private void nudManualLimitContracts_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.ManualLimitContracts = (int)nudManualLimitContracts.Value;
            SaveSettings();
        }

        private void nudManualLimitPrice_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.ManualLimitPrice = nudManualLimitPrice.Value;
            SaveSettings();
        }

        private void chkManualLimitReduceOnly_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.ManualLimitReduceOnly = chkManualLimitReduceOnly.Checked;
            SaveSettings();
        }

        private void chkManualLimitPostOnly_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.ManualLimitPostOnly = chkManualLimitPostOnly.Checked;
            SaveSettings();
        }

        private void chkManualLimitCancelWhileOrdering_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.ManualLimitCancelOpenOrders = chkManualLimitCancelWhileOrdering.Checked;
            SaveSettings();
        }

        private void btnManualLimitBuy_Click(object sender, EventArgs e)
        {
            if (chkManualLimitCancelWhileOrdering.Checked)
            {
                bitmex.CancelAllOpenOrders(ActiveInstrument.Symbol);
            }
            bitmex.LimitOrder(ActiveInstrument.Symbol, "Buy", (int)nudManualLimitContracts.Value, nudManualLimitPrice.Value, chkManualLimitReduceOnly.Checked, chkManualLimitPostOnly.Checked, chkManualLimitHiddenOrder.Checked);
            Console.WriteLine("Limit buy done");
        }

        private void btnManualLimitSell_Click(object sender, EventArgs e)
        {
            if (chkManualLimitCancelWhileOrdering.Checked)
            {
                bitmex.CancelAllOpenOrders(ActiveInstrument.Symbol);
            }
            bitmex.LimitOrder(ActiveInstrument.Symbol, "Sell", (int)nudManualLimitContracts.Value, nudManualLimitPrice.Value, chkManualLimitReduceOnly.Checked, chkManualLimitPostOnly.Checked, chkManualLimitHiddenOrder.Checked);
            Console.WriteLine("Limit sell done");
        }

        private void chkManualLimitHiddenOrder_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.ManualLimitHiddenOrder = chkManualLimitHiddenOrder.Checked;
            SaveSettings();
        }

        private void nudManualLimitPercentModifier1_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.ManualLimitPercentModifier1 = nudManualLimitPercentModifier1.Value;
            SaveSettings();
        }

        private void nudManualLimitPercentModifier2_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.ManualLimitPercentModifier2 = nudManualLimitPercentModifier2.Value;
            SaveSettings();
        }

        private void nudManualLimitPercentModifier3_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.ManualLimitPercentModifier3 = nudManualLimitPercentModifier3.Value;
            SaveSettings();
        }

        private void nudManualLimitPercentModifier4_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.ManualLimitPercentModifier4 = nudManualLimitPercentModifier4.Value;
            SaveSettings();
        }

        private decimal PercentageChange(bool Increase, decimal Base, decimal Change, decimal TickSize)
        {
            decimal Result = 0;
            decimal Adjustment = Base * Change;

            if (Increase)
            {
                // increase
                Result = Base + Adjustment;

            }
            else
            {
                // decrease
                Result = Base - Adjustment;
            }

            decimal Remainder = Result % TickSize;
            Result = Result - Remainder; // Remove any remainder to avoid issues.

            return Result;
        }

        private void UpdateManualLimitPriceFromPercentModifier(bool Increase, decimal Change)
        {
            Change = Change / 100; // Values are shown as %s, so must divide by 100

            if (chkManualLimitPercentModifierUseCurrentPrice.Checked)
            {
                nudManualLimitPrice.Value = PercentageChange(Increase, nudCurrentPrice.Value, Change, ActiveInstrument.TickSize);
            }
            else
            {
                nudManualLimitPrice.Value = PercentageChange(Increase, nudManualLimitPrice.Value, Change, ActiveInstrument.TickSize);
            }
        }

        private void btnManualLimitPercentModifier1Down_Click(object sender, EventArgs e)
        {
            UpdateManualLimitPriceFromPercentModifier(false, nudManualLimitPercentModifier1.Value);
        }

        private void btnManualLimitPercentModifier1Up_Click(object sender, EventArgs e)
        {
            UpdateManualLimitPriceFromPercentModifier(true, nudManualLimitPercentModifier1.Value);
        }

        private void btnManualLimitPercentModifier2Down_Click(object sender, EventArgs e)
        {
            UpdateManualLimitPriceFromPercentModifier(false, nudManualLimitPercentModifier2.Value);
        }

        private void btnManualLimitPercentModifier2Up_Click(object sender, EventArgs e)
        {
            UpdateManualLimitPriceFromPercentModifier(true, nudManualLimitPercentModifier2.Value);
        }

        private void btnManualLimitPercentModifier3Down_Click(object sender, EventArgs e)
        {
            UpdateManualLimitPriceFromPercentModifier(false, nudManualLimitPercentModifier3.Value);
        }

        private void btnManualLimitPercentModifier3Up_Click(object sender, EventArgs e)
        {
            UpdateManualLimitPriceFromPercentModifier(true, nudManualLimitPercentModifier3.Value);
        }

        private void btnManualLimitPercentModifier4Down_Click(object sender, EventArgs e)
        {
            UpdateManualLimitPriceFromPercentModifier(false, nudManualLimitPercentModifier4.Value);
        }

        private void btnManualLimitPercentModifier4Up_Click(object sender, EventArgs e)
        {
            UpdateManualLimitPriceFromPercentModifier(true, nudManualLimitPercentModifier4.Value);
        }

        private void chkManualLimitPercentModifierUseCurrentPrice_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.ManualLimitPercentModifierUseCurrentPrice = chkManualLimitPercentModifierUseCurrentPrice.Checked;
            SaveSettings();
        }

        private void btnManualLimitCancelOpenOrders_Click(object sender, EventArgs e)
        {
            bitmex.CancelAllOpenOrders(ActiveInstrument.Symbol);
        }

#endregion

#region Limit Now

        private void LimitNowAmendBuyThreadAction()
        {
            while(true)
            {
                //Console.WriteLine("Buy waiting");
                UpdateLimitNowBuys.WaitOne();
                //Console.WriteLine("Buy waited");
                if (LimitNowBuyOrders.Count > 0)
                {
                    decimal Price = LimitNowGetOrderPrice("Buy");
                    if (CheckOrderPrices(LimitNowBuyOrders, Price))
                    {
                        tmrLimitNowBuy_Tick(this, EventArgs.Empty);
                    }
                }
                UpdateLimitNowBuys.Reset();
            }
        }

        private void LimitNowAmendSellThreadAction()
        {
            while(true)
            {
                //Console.WriteLine("Sell waiting");
                UpdateLimitNowSells.WaitOne();
                //Console.WriteLine("Sell waited");
                if (LimitNowSellOrders.Count > 0)
                {
                    decimal Price = LimitNowGetOrderPrice("Sell");
                    if (CheckOrderPrices(LimitNowSellOrders, Price))
                    {
                        tmrLimitNowSell_Tick(this, EventArgs.Empty);
                    }
                }
                UpdateLimitNowSells.Reset();
            }
        }

        private void nudLimitNowBuyContracts_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.LimitNowBuyContracts = (int)nudLimitNowBuyContracts.Value;
            SaveSettings();
        }

        private void btnLimitNowBuy_Click(object sender, EventArgs e)
        {
            LimitNowBuyOrders = LimitNowStartBuying();
        }

        private void btnLimitNowSell_Click(object sender, EventArgs e)
        {
            LimitNowSellOrders = LimitNowStartSelling();
        }

        private void tmrLimitNowBuy_Tick(object sender, EventArgs e)
        {
            List<Order> LimitNowOrderResult = LimitNowAmendBuying();
            LimitNowBuyOrders = LimitNowOrderResult;
            // Timer should stop if there are no orders left to amend.
            if (LimitNowOrderResult.Any())
            {
                if (LimitNowOrderResult.FirstOrDefault().OrdStatus == "Filled")
                {
                    LimitNowStopBuying();

                }
            }
            else
            {
                // Order no longer available, stop it
                LimitNowStopBuying();
            }
        }

        private List<Order> LimitNowStartBuying()
        {
            LimitNowBuyOrders.Clear();
            // Initial order
            decimal Price = LimitNowGetOrderPrice("Buy");
            decimal StopLossDelta = ActiveInstrument.TickSize * LimitNowStopLossBuyDelta;
            List<Order> LimitNowOrderResult = null;
            if (chkLimitNowOrderBookDetectionBuy.Checked && false)
            {
                LimitNowOrderResult = bitmex.LimitNowOrderBreakout(ActiveInstrument.Symbol, "Buy", (int)nudLimitNowBuyContracts.Value, Price, chkLimitNowBuyReduceOnly.Checked, true, false);
            }
            else 
            {
                if (chkLimitNowStopLossBuy.Checked)
                {
                    LimitNowOrderResult = bitmex.LimitNowOrderSafety(ActiveInstrument.Symbol, "Buy", (int)nudLimitNowBuyContracts.Value, Price, StopLossDelta, chkLimitNowBuyReduceOnly.Checked, true, false);
                }
                else
                {
                    LimitNowOrderResult = bitmex.LimitNowOrder(ActiveInstrument.Symbol, "Buy", (int)nudLimitNowBuyContracts.Value, Price, chkLimitNowBuyReduceOnly.Checked, true, false);
                }
            }
            btnLimitNowBuyCancel.Visible = true;
            btnLimitNowBuy.Visible = false;

            
            if (LimitNowOrderResult.Any())
            {
                // Start buy timer
                if (LimitNowOrderResult.Count == 2)
                {
                    Order primary = LimitNowOrderResult.Where(x => !string.IsNullOrEmpty(x.ContingencyType)).FirstOrDefault();
                    LimitNowBuyOrderId = primary.OrderId;
                    //Console.WriteLine("Primary Buy:" + LimitNowBuyOrderId);
                    LimitNowBuyOrderPrice = Price;
                }
                else
                {
                    LimitNowBuyOrderId = LimitNowOrderResult.FirstOrDefault().OrderId;
                    LimitNowBuyOrderPrice = Price;
                }
                if (!chkLimitNowOrderBookDetectionBuy.Checked)
                    tmrLimitNowBuy.Start();
            }
            

            return LimitNowOrderResult;
        }

        private List<Order> LimitNowStartSelling()
        {
            LimitNowSellOrders.Clear();
            List<Order> LimitNowOrderResult = null;
            // Initial order
            decimal Price = LimitNowGetOrderPrice("Sell");
            decimal StopLossDelta = ActiveInstrument.TickSize * LimitNowStopLossSellDelta;
            if (chkLimitNowOrderBookDetectionSell.Checked && false) // for testing only
            {
                LimitNowOrderResult = bitmex.LimitNowOrderBreakout(ActiveInstrument.Symbol, "Sell", (int)nudLimitNowSellContracts.Value, Price, chkLimitNowSellReduceOnly.Checked, true, false);
            }
            else
            {
                if (chkLimitNowStopLossSell.Checked)
                {
                    LimitNowOrderResult = bitmex.LimitNowOrderSafety(ActiveInstrument.Symbol, "Sell", (int)nudLimitNowSellContracts.Value, Price, StopLossDelta, chkLimitNowSellReduceOnly.Checked, true, false);
                }
                else
                {
                    LimitNowOrderResult = bitmex.LimitNowOrder(ActiveInstrument.Symbol, "Sell", (int)nudLimitNowSellContracts.Value, Price, chkLimitNowSellReduceOnly.Checked, true, false);
                }
            }
            btnLimitNowSellCancel.Visible = true;
            btnLimitNowSell.Visible = false;

            
            if (LimitNowOrderResult.Any())
            {
                // Start buy timer
                //LimitNowSellOrderId = LimitNowOrderResult.FirstOrDefault().OrderId;
                //LimitNowSellOrderPrice = Price;
                if (LimitNowOrderResult.Count == 2)
                {
                    Order primary = LimitNowOrderResult.Where(x => !string.IsNullOrEmpty(x.ContingencyType)).FirstOrDefault();
                    LimitNowSellOrderId = primary.OrderId;
                    //Console.WriteLine("Primary Sell:" + LimitNowSellOrderId);
                    LimitNowSellOrderPrice = Price;
                }
                else
                {
                    LimitNowSellOrderId = LimitNowOrderResult.FirstOrDefault().OrderId;
                    LimitNowSellOrderPrice = Price;
                }
                if (!chkLimitNowOrderBookDetectionSell.Checked)
                    tmrLimitNowSell.Start();
            }


            return LimitNowOrderResult;
        }

        private List<Order> LimitNowAmendBuying()
        {
            //Console.WriteLine("LimitNow Amend Buying");
            decimal Price = LimitNowGetOrderPrice("Buy");
            int Contracts = (int)nudLimitNowBuyContracts.Value;
            List<Order> LimitNowOrderResult = new List<Order>();
            if (Price != 0 /*&& Price != LimitNowBuyOrderPrice*/)
            {
                //Console.WriteLine("LimitNow Amend Buying:" + LimitNowBuyOrders.Count + ":" + Price);
                if (LimitNowBuyOrders.Count == 1)
                {
                    //Console.WriteLine("Doing 1");
                    LimitNowOrderResult = bitmex.LimitNowAmendOrder(LimitNowBuyOrderId, Price, Contracts);
                }
                else
                {
                    List<OrderAmend> orders = new List<OrderAmend>();
                    for (int i = 0; i < LimitNowBuyOrders.Count; i++)
                    {
                        LimitNowBuyOrders[i].Price = Price;
                        LimitNowBuyOrders[i].OrderQty = Contracts;
                        if (LimitNowBuyOrders[i].ContingencyType == "")
                        {
                            LimitNowBuyOrders[i].Price = Price - ActiveInstrument.TickSize * LimitNowStopLossBuyDelta;
                        }
                        //LimitNowBuyOrders[i].OrdStatus = "";
                        OrderAmend order = new OrderAmend(LimitNowBuyOrders[i]);
                        orders.Add(order);
                    }
                    string orderlist = JsonConvert.SerializeObject(orders);
                    string res = bitmex.AmendBulkOrder(orderlist);
                    if (res.Contains("Error"))
                    {
                        return LimitNowOrderResult;
                    }
                    //Console.WriteLine("Amend Buying return:" + res);
                    LimitNowOrderResult = (JsonConvert.DeserializeObject<List<Order>>(res));
                }
                LimitNowBuyOrderPrice = Price;
                //LimitNowOrderResult = bitmex.LimitNowAmendOrder(LimitNowBuyOrderId, Price, Contracts);
            }
            else
            {
                //Console.WriteLine("Price is zero?!" + Price);
                LimitNowOrderResult.Add(new Order());
            }
            return LimitNowOrderResult;
        }

        private List<Order> LimitNowAmendSelling()
        {
            decimal Price = LimitNowGetOrderPrice("Sell");
            int Contracts = (int)nudLimitNowSellContracts.Value;
            List<Order> LimitNowOrderResult = new List<Order>();
            if (Price != 0 /*&& Price != LimitNowSellOrderPrice*/)
            {
                //Console.WriteLine("LimitNow Amend Selling:" + LimitNowSellOrders.Count+":"+ LimitNowSellOrderPrice+"=>"+Price);
                if (LimitNowSellOrders.Count == 1)
                {
                    LimitNowOrderResult = bitmex.LimitNowAmendOrder(LimitNowSellOrderId, Price, Contracts);
                }
                else
                {
                    List<OrderAmend> orders = new List<OrderAmend>();
                    for (int i = 0; i < LimitNowSellOrders.Count; i++)
                    {
                        LimitNowSellOrders[i].Price = Price;
                        LimitNowSellOrders[i].OrderQty = Contracts;
                        if (LimitNowSellOrders[i].ContingencyType == "")
                        {
                            LimitNowSellOrders[i].Price = Price + ActiveInstrument.TickSize * LimitNowStopLossSellDelta;
                        }
                        //LimitNowSellOrders[i].OrdStatus = "";
                        OrderAmend order = new OrderAmend(LimitNowSellOrders[i]);
                        orders.Add(order);
                    }
                    string orderlist = JsonConvert.SerializeObject(orders);
                    //Console.WriteLine("Amend Selling orders:" + orderlist+":"+Price);
                    string res = bitmex.AmendBulkOrder(orderlist);
                    //Console.WriteLine("Amend Selling return:" + res);
                    if (res.Contains("Error"))
                    {
                        return LimitNowOrderResult;
                    }
                    LimitNowOrderResult = (JsonConvert.DeserializeObject<List<Order>>(res));
                }
                LimitNowSellOrderPrice = Price;
            }
            else
            {
                //Console.WriteLine("Price is zero?!" + Price);
                LimitNowOrderResult.Add(new Order());
            }
            return LimitNowOrderResult;

        }

        private void tmrLimitNowSell_Tick(object sender, EventArgs e)
        {
            //Console.WriteLine("Checking to see if the order has completely filled");
            List<Order> LimitNowOrderResult = LimitNowAmendSelling();
            LimitNowSellOrders = LimitNowOrderResult;
            // Timer should stop if there are no orders left to amend.
            if (LimitNowOrderResult.Any())
            {
                if (LimitNowOrderResult.FirstOrDefault().OrdStatus == "Filled")
                {
                    LimitNowStopSelling();
                }
            }
            else
            {
                // Order no longer available, stop it
                LimitNowStopSelling();
            }

        }

        bool CheckOrderPrices(List<Order> orders, Decimal price)
        {
            if (price == Decimal.Zero)
                return false;
            for (int i = 0; i < orders.Count; i++)
            {
                if (orders[i].Price != price && orders.Count == 1)
                {
                    //Log("Price changed:" + orders[i].Price + "=>" + price);
                    return true;
                }
                else if (orders[i].Price != price && orders[i].ContingencyType == "OneTriggersTheOther")
                {
                    return true;
                }
            }
            return false;
        }

        private void UpdateOrders()
        {
            //Console.WriteLine("Trade Update");
            if (chkLimitNowOrderBookDetectionSell.Checked)
            {
#if USE_SEPARATE_THREADS
                UpdateLimitNowSells.Set();
#else
                if (LimitNowSellOrders.Count > 0)
                {
                    decimal Price = LimitNowGetOrderPrice("Sell");
                    if (CheckOrderPrices(LimitNowSellOrders, Price))
                    {
                        //Log("Sell price changed");
                        tmrLimitNowSell_Tick(this, EventArgs.Empty);
                    }
                }
#endif
            }
            if (chkLimitNowOrderBookDetectionBuy.Checked)
            {
#if USE_SEPARATE_THREADS
                UpdateLimitNowBuys.Set();
#else
                if (LimitNowBuyOrders.Count > 0)
                {
                    decimal Price = LimitNowGetOrderPrice("Buy");
                    if (CheckOrderPrices(LimitNowBuyOrders, Price))
                    {
                        //Log("Buy price changed");
                        tmrLimitNowBuy_Tick(this, EventArgs.Empty);
                }
            }
            else
            {
            }
#endif
            }
        }
        private void LimitNow_TradeUpdate(object sender, EventArgs e)
        {
            //Console.WriteLine("Trade Update");
            UpdateOrders();
        }

        private void LimitNow_OrderUpdate(object sender, EventArgs e)
        {
            //Console.WriteLine("Order Update");
            UpdateOrders();
        }


        private void nudLimitNowBuyTicksFromCenter_ValueChanged(object sender, EventArgs e)
        {
            LimitNowBuyTicksFromCenter = nudLimitNowBuyTicksFromCenter.Value;
            Properties.Settings.Default.LimitNowBuyTicksFromCenter = (int)nudLimitNowBuyTicksFromCenter.Value;
            SaveSettings();
        }

        private void nudLimitNowBuyDelay_ValueChanged(object sender, EventArgs e)
        {
            tmrLimitNowBuy.Interval = (int)nudLimitNowBuyDelay.Value;

            Properties.Settings.Default.LimitNowBuyDelay = (int)nudLimitNowBuyDelay.Value;
            SaveSettings();
        }

        private void btnLimitNowBuyCancel_Click(object sender, EventArgs e)
        {
            /*
            for (int i=0;i<LimitNowBuyOrders.Count;i++)
            {
                bitmex.CancelOrder(LimitNowBuyOrders[i].OrderId);
            }
            LimitNowBuyOrders.Clear();
            */
            //bitmex.CancelOrder(LimitNowBuyOrderId);
            chkLimitNowBuyContinue.Checked = false;
            LimitNowStopBuying();
        }

        private void CancelAllBuys()
        {
            for (int i = 0; i < LimitNowBuyOrders.Count; i++)
            {
                bitmex.CancelOrder(LimitNowBuyOrders[i].OrderId);
            }
            LimitNowBuyOrders.Clear();
            LimitNowBuyOrderPrice = 0;
        }

        private void btnLimitNowSellCancel_Click(object sender, EventArgs e)
        {
            /*
            for (int i = 0; i < LimitNowSellOrders.Count; i++)
            {
                bitmex.CancelOrder(LimitNowSellOrders[i].OrderId);
            }
            LimitNowSellOrders.Clear();
            */
            //bitmex.CancelOrder(LimitNowSellOrderId);
            chkLimitNowSellContinue.Checked = false;
            LimitNowStopSelling();
        }

        private void CancelAllSells()
        {
            for (int i = 0; i < LimitNowSellOrders.Count; i++)
            {
                bitmex.CancelOrder(LimitNowSellOrders[i].OrderId);
            }
            LimitNowSellOrders.Clear();
            LimitNowSellOrderPrice = 0;
        }

        private void LimitNowStopBuying()
        {
            CancelAllBuys();
            tmrLimitNowBuy.Stop();
            LimitNowBuyOrderId = "";
            btnLimitNowBuyCancel.Invoke((MethodInvoker)(()=>
                {
                    btnLimitNowBuyCancel.Visible = false;
                    btnLimitNowBuy.Visible = true;
                    if (chkLimitNowBuyContinue.Checked)
                    {
                        LimitNowStartBuying();
                    }
                }));

        }

        private void LimitNowStopSelling()
        {
            //Console.WriteLine("Sell timer stop selling");
            CancelAllSells();
            tmrLimitNowSell.Stop();
            LimitNowSellOrderId = "";
            btnLimitNowSellCancel.Invoke((MethodInvoker)(() =>
            {
                btnLimitNowSellCancel.Visible = false;
                btnLimitNowSell.Visible = true;

                if (chkLimitNowSellContinue.Checked)
                {
                    LimitNowStartSelling();
                }
            }));
        }

        private void chkLimitNowBuyContinue_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.LimitNowBuyContinue = chkLimitNowBuyContinue.Checked;
            SaveSettings();
        }

        private void nudLimitNowSellContracts_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.LimitNowSellContracts = (int)nudLimitNowSellContracts.Value;
            SaveSettings();
        }

        private void nudLimitNowSellTicksFromCenter_ValueChanged(object sender, EventArgs e)
        {
            LimitNowSellTicksFromCenter = nudLimitNowSellTicksFromCenter.Value;
            Properties.Settings.Default.LimitNowSellTicksFromCenter = (int)nudLimitNowSellTicksFromCenter.Value;
            SaveSettings();
        }

        private void nudLimitNowSellDelay_ValueChanged(object sender, EventArgs e)
        {
            tmrLimitNowSell.Interval = (int)nudLimitNowSellDelay.Value;

            Properties.Settings.Default.LimitNowSellDelay = (int)nudLimitNowSellDelay.Value;
            SaveSettings();
        }

        private void chkLimitNowSellContinue_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.LimitNowSellContinue = chkLimitNowSellContinue.Checked;
            SaveSettings();
        }

        private void ddlLimitNowBuyMethod_SelectedIndexChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.LimitNowBuyMethod = (string)ddlLimitNowBuyMethod.SelectedItem;
            SaveSettings();
            LimitNowBuyMethod = (string)ddlLimitNowBuyMethod.SelectedItem;

            if ((string)ddlLimitNowBuyMethod.SelectedItem == "Best Price")
            {
                nudLimitNowBuyTicksFromCenter.Enabled = false;
            }
            else if ((string)ddlLimitNowBuyMethod.SelectedItem == "Quick Fill")
            {
                nudLimitNowBuyTicksFromCenter.Enabled = true;
            }

        }

        private void ddlLimitNowSellMethod_SelectedIndexChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.LimitNowSellMethod = (string)ddlLimitNowSellMethod.SelectedItem;
            SaveSettings();
            LimitNowSellMethod = (string)ddlLimitNowSellMethod.SelectedItem;

            if ((string)ddlLimitNowSellMethod.SelectedItem == "Best Price")
            {
                nudLimitNowSellTicksFromCenter.Enabled = false;
            }
            else if ((string)ddlLimitNowSellMethod.SelectedItem == "Quick Fill")
            {
                nudLimitNowSellTicksFromCenter.Enabled = true;
            }

        }

        private decimal LimitNowGetOrderPrice(string Side,bool UseL2 = true)
        {
            decimal Price = Decimal.Zero;
            try
            {
                switch (Side)
                {
                    case "Buy":
                        if (LimitNowBuyMethod == "Best Price")
                        {
                            decimal LowestAsk = decimal.Zero;
                            decimal HighestBid = decimal.Zero;
#if !USE_L2
                            LowestAsk = OrderBookTopAsks.OrderBy(a => a.Price).FirstOrDefault().Price;
                            HighestBid = OrderBookTopBids.OrderByDescending(a => a.Price).FirstOrDefault().Price;
#else                        
                            LowestAsk = OrderBookL2Asks.ElementAt(0).Value.Price;
                            HighestBid = OrderBookL2Bids.ElementAt(0).Value.Price;
#endif
                            if (HighestBid != LimitNowBuyOrderPrice) // Our price isn't the highest in book
                            {
                                if (LowestAsk - HighestBid > ActiveInstrument.TickSize) // More than 1 tick size spread
                                {
                                    Price = HighestBid + ActiveInstrument.TickSize;
                                }
                                else
                                {
                                    Price = LowestAsk - ActiveInstrument.TickSize;
                                }
                            }
                            else
                            {
                                Log("Buy Best Price Unable to set Price:Highest bid is the same as asking:" + HighestBid + ":" + LimitNowBuyOrderPrice);
                            }

                        }
                        else if (LimitNowBuyMethod == "Quick Fill")
                        {
#if !USE_L2
                            Price = OrderBookTopAsks.OrderBy(a => a.Price).FirstOrDefault().Price - (ActiveInstrument.TickSize * LimitNowBuyTicksFromCenter);
#else
                            Price = OrderBookL2Asks.ElementAt(0).Value.Price - (ActiveInstrument.TickSize * LimitNowBuyTicksFromCenter);
#endif
                        }
                        else if (LimitNowBuyMethod == "Order Level")
                        {
                            if (!UseL2)
                            {
                                List<OrderBook> sorted = OrderBookTopBids.OrderByDescending(a => a.Price).ToList();
                                if (LimitNowBuyLevel<sorted.Count)
                                {
                                    Price = sorted[LimitNowBuyLevel].Price;
                                }
                            }
                            else if (L2Initialized)
                            {
                                Price = OrderBookL2Bids.ElementAt(LimitNowBuyLevel).Value.Price;
                            }
                        }
                        break;
                    case "Sell":
                        if (LimitNowSellMethod == "Best Price")
                        {
                            decimal LowestAsk = decimal.Zero;
                            decimal HighestBid = decimal.Zero;
#if !USE_L2
                            LowestAsk = OrderBookTopAsks.OrderBy(a => a.Price).FirstOrDefault().Price;
                            HighestBid = OrderBookTopBids.OrderByDescending(a => a.Price).FirstOrDefault().Price;
#else
                            LowestAsk = OrderBookL2Asks.ElementAt(0).Value.Price;
                            HighestBid = OrderBookL2Bids.ElementAt(0).Value.Price;
#endif
                            if (LowestAsk != LimitNowSellOrderPrice) // Our price isn't the highest in book
                            {
                                if (LowestAsk - HighestBid > ActiveInstrument.TickSize) // More than 1 tick size spread
                                {
                                    Price = LowestAsk - ActiveInstrument.TickSize;
                                }
                                else
                                {
                                    Price = HighestBid + ActiveInstrument.TickSize;
                                }
                            }
                            else
                            {
                                Log("Sell Best Price Unable to set Price:Lowest ask is the same as asking:" + LowestAsk + ":" + LimitNowSellOrderPrice);
                            }
                        }
                        else if (LimitNowSellMethod == "Quick Fill")
                        {
#if !USE_L2
                            Price = OrderBookTopBids.OrderByDescending(a => a.Price).FirstOrDefault().Price + (ActiveInstrument.TickSize * LimitNowSellTicksFromCenter);
#else
                            Price = OrderBookL2Bids.ElementAt(0).Value.Price + (ActiveInstrument.TickSize * LimitNowSellTicksFromCenter);
#endif
                        }
                        else if (LimitNowSellMethod == "Order Level")
                        {
                            if (!UseL2)
                            {
                                List<OrderBook> sorted = OrderBookTopAsks.OrderBy(a => a.Price).ToList();
                                if (LimitNowSellLevel<sorted.Count)
                                {
                                    Price = sorted[LimitNowSellLevel].Price;
                                }
                            }
                            else if (L2Initialized)
                            {
                                Price = OrderBookL2Asks.ElementAt(LimitNowSellLevel).Value.Price;
                            }
                        }
                        break;
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {

            }

            return Price;
        }

        private void TabControl_SelectedIndexChanged(object sender, System.EventArgs e)
        {
            //Console.WriteLine("Tab selected:" + TabControl.SelectedIndex);
            Properties.Settings.Default.TabSelection = TabControl.SelectedIndex;
            SaveSettings();
        }


#endregion



        private void TriggerAlerts()
        {
            //if(Alerts.Where(a => a.Triggered == false).Any())
            //{

            //    foreach(Alert a in Alerts)
            //    {
            //        a.Triggered = true;
            //        switch (a.Side)
            //        {
            //            case "Above":
            //                if(Prices[a.Symbol] > a.Price)
            //                {
            //                    MessageBox.Show("Alert! " + a.Symbol + " price is now above " + a.Price.ToString() + ".");
            //                }
            //                break;
            //            case "Below":
            //                if (Prices[a.Symbol] < a.Price)
            //                {
            //                    MessageBox.Show("Alert! " + a.Symbol + " price is now below " + a.Price.ToString() + ".");
            //                }
            //                break;
            //        }



            //    }
            //}
        }

        private void chkLimitNowBuyReduceOnly_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.LimitNowBuyReduceOnly = chkLimitNowBuyReduceOnly.Checked;
            SaveSettings();
        }

        private void chkLimitNowSellReduceOnly_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.LimitNowSellReduceOnly = chkLimitNowSellReduceOnly.Checked;
            SaveSettings();
        }

        private void Bot_FormClosed(object sender, FormClosedEventArgs e)
        {
            Environment.Exit(0);
        }

        private void pictureBox2_Click(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("https://www.youtube.com/BigBits?sub_confirmation=1");
        }


        private void TrailingStopLimitOrder()
        {
            // Initial order
            string StopSide = "";
            int StopSize = 0;
            decimal OrderPrice = 0;

            if (chkStopTrailingCloseInFull.Checked)
            {
                StopSize = (int)SymbolPosition.CurrentQty;
            }
            else
            {
                StopSize = (int)nudStopTrailingContracts.Value;
            }

            if (SymbolPosition.CurrentQty > 0)
            {
                OrderPrice = (decimal)SymbolPosition.TrailingStopPrice + nudStopTrailingLimitOffset.Value;
                StopSide = "Sell";
            }
            else if (SymbolPosition.CurrentQty < 0)
            {
                OrderPrice = (decimal)SymbolPosition.TrailingStopPrice + nudStopTrailingLimitOffset.Value;
                StopSide = "Buy";
                StopSize = StopSize * -1;
            }

            bitmex.LimitNowOrder(ActiveInstrument.Symbol, StopSide, StopSize, OrderPrice, true, false, false);

        }





        private void ddlStopTrailingMethod_SelectedIndexChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.TrailingStopMethod = (string)ddlStopTrailingMethod.SelectedItem;
            SaveSettings();
            TrailingStopMethod = (string)ddlStopTrailingMethod.SelectedItem;

            if (TrailingStopMethod == "Limit")
            {
                nudStopTrailingLimitOffset.Visible = true;
                lblTrailingStopLimitOffset.Visible = true;
                lblTrailingStopLimitPricePreviewLabel.Visible = true;
                lblStopTrailingLimitOffsetPrice.Visible = true;
            }
            else
            {
                nudStopTrailingLimitOffset.Visible = false;
                lblTrailingStopLimitOffset.Visible = false;
                lblTrailingStopLimitPricePreviewLabel.Visible = false;
                lblStopTrailingLimitOffsetPrice.Visible = false;
            }
        }

        private void nudStopTrailingPrice_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.TrailingStopTrail = nudStopTrailingTrail.Value;
            SaveSettings();
            UpdateTrailingStopData(ActiveInstrument.Symbol, Prices[ActiveInstrument.Symbol]);
        }

        private void metroToggle1_CheckedChanged(object sender, EventArgs e)
        {

        }

        private void UpdateTrailingStopData(string Symbol, decimal Price)
        {
            if (SymbolPosition.HighestPriceSinceOpen == null)
            {
                SymbolPosition.HighestPriceSinceOpen = Price;
            }
            if (SymbolPosition.LowestPriceSinceOpen == null)
            {
                SymbolPosition.LowestPriceSinceOpen = Price;
            }


            if (Price > SymbolPosition.HighestPriceSinceOpen)
            {
                SymbolPosition.HighestPriceSinceOpen = Price;
            }
            if (Price < SymbolPosition.LowestPriceSinceOpen)
            {
                SymbolPosition.LowestPriceSinceOpen = Price;
            }



            if (SymbolPosition.CurrentQty > 0)
            {


                if (chkTrailingStopEnabled.Checked)
                {
                    SymbolPosition.TrailingStopPrice = (decimal)SymbolPosition.HighestPriceSinceOpen - nudStopTrailingTrail.Value;
                }
                else
                {
                    SymbolPosition.TrailingStopPrice = Prices[ActiveInstrument.Symbol] - nudStopTrailingTrail.Value;
                }
                lblStopTraillingPrice.Invoke(new Action(() => lblStopTraillingPrice.Text = SymbolPosition.TrailingStopPrice.ToString()));
                lblStopTrailingLimitOffsetPrice.Invoke(new Action(() => lblStopTrailingLimitOffsetPrice.Text = ((decimal)SymbolPosition.TrailingStopPrice + nudStopTrailingLimitOffset.Value).ToString()));

            }
            else if (SymbolPosition.CurrentQty < 0)
            {
                // Short, so set stop price = lowest + distance
                if (chkTrailingStopEnabled.Checked)
                {
                    SymbolPosition.TrailingStopPrice = (decimal)SymbolPosition.LowestPriceSinceOpen + nudStopTrailingTrail.Value;
                }
                else
                {
                    SymbolPosition.TrailingStopPrice = Prices[ActiveInstrument.Symbol] + nudStopTrailingTrail.Value;
                }
                lblStopTraillingPrice.Invoke(new Action(() => lblStopTraillingPrice.Text = SymbolPosition.TrailingStopPrice.ToString()));
                lblStopTrailingLimitOffsetPrice.Invoke(new Action(() => lblStopTrailingLimitOffsetPrice.Text = ((decimal)SymbolPosition.TrailingStopPrice + nudStopTrailingLimitOffset.Value).ToString()));

            }
            else
            {
                lblStopTraillingPrice.Invoke(new Action(() => lblStopTraillingPrice.Text = "+/- " + nudStopTrailingTrail.Value.ToString()));
                lblStopTrailingLimitOffsetPrice.Invoke(new Action(() => lblStopTrailingLimitOffsetPrice.Text = "+/- " + (Math.Abs(nudStopTrailingLimitOffset.Value) + nudStopTrailingTrail.Value).ToString()));
            }
        }

        private void ProcessTrailingStop(string Symbol, decimal Price)
        {


            // Lets also check to see if we need to execute a market sell
            //   LIMIT STOPS are handled with the timer.
            if (SymbolPosition.CurrentQty > 0)
            {
                // We are long - close if price comes back to and below trailing stop
                if (Price <= SymbolPosition.TrailingStopPrice)
                {
                    if (TrailingStopMethod == "Market")
                    {
                        if (chkStopTrailingCloseInFull.Checked)
                        {
                            MarketClosePosition();
                        }
                        else
                        {
                            bitmex.MarketOrder(Symbol, "Sell", (int)nudStopTrailingContracts.Value);
                        }
                    }
                    else if (TrailingStopMethod == "Limit")
                    {
                        TrailingStopLimitOrder();

                    }
                    chkTrailingStopEnabled.Invoke(new Action(() => chkTrailingStopEnabled.Checked = false));
                }
            }
            else if (SymbolPosition.CurrentQty < 0)
            {
                // We are short - close if price comes back to and above trailing stop
                if (Price >= SymbolPosition.TrailingStopPrice)
                {
                    if (TrailingStopMethod == "Market")
                    {
                        if (chkStopTrailingCloseInFull.Checked)
                        {
                            MarketClosePosition();
                        }
                        else
                        {
                            bitmex.MarketOrder(Symbol, "Buy", (int)nudStopTrailingContracts.Value);
                        }
                    }
                    else if (TrailingStopMethod == "Limit")
                    {
                        TrailingStopLimitOrder();
                    }
                    chkTrailingStopEnabled.Invoke(new Action(() => chkTrailingStopEnabled.Checked = false));
                }
            }

        }

        private void chkTrailingStopEnabled_CheckedChanged(object sender, EventArgs e)
        {
            if (chkTrailingStopEnabled.Checked)
            {
                if (SymbolPosition.CurrentQty != 0)
                {
                    SymbolPosition.HighestPriceSinceOpen = Prices[ActiveInstrument.Symbol];
                    SymbolPosition.LowestPriceSinceOpen = Prices[ActiveInstrument.Symbol];
                }
                else
                {
                    chkTrailingStopEnabled.Checked = false;
                }

            }

        }


        private void lblDonateAddress_Click(object sender, EventArgs e)
        {
            Clipboard.SetText("33biFCDFEZn3hLJcGKLR5Muu9oeRWBAFEX");
        }

        private void nudStopTrailingLimitOffset_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.TrailingStopLimitOffset = nudStopTrailingLimitOffset.Value;
            SaveSettings();
            UpdateTrailingStopData(ActiveInstrument.Symbol, Prices[ActiveInstrument.Symbol]);
        }

        private void nudStopTrailingContracts_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.TrailingStopContracts = (int)nudStopTrailingContracts.Value;
            SaveSettings();
        }

        private void chkStopTrailingCloseInFull_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.TrailingStopCloseInFull = chkStopTrailingCloseInFull.Checked;
            SaveSettings();

            if (chkStopTrailingCloseInFull.Checked)
            {
                nudStopTrailingContracts.Enabled = false;
            }
            else
            {
                nudStopTrailingContracts.Enabled = true;
            }
        }

        private void pictureBox3_Click(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("https://twitter.com/BigBitsYouTube");
        }

        private void chkLimitNowStopLossBuy_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.LimitNowStopLossBuy = chkLimitNowStopLossBuy.Checked;
            SaveSettings();
        }

        private void chkLimitNowStopLossSell_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.LimitNowStopLossSell = chkLimitNowStopLossSell.Checked;
            SaveSettings();
        }

        private void nudLimitNowStopLossBuyDelta_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.LimitNowStopLossBuyDelta = nudLimitNowStopLossBuyDelta.Value;
            LimitNowStopLossBuyDelta = nudLimitNowStopLossBuyDelta.Value;
            SaveSettings();
        }

        private void nudLimitNowStopLossSellDelta_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.LimitNowStopLossSellDelta = nudLimitNowStopLossSellDelta.Value;
            LimitNowStopLossSellDelta = nudLimitNowStopLossSellDelta.Value;
            SaveSettings();
        }

        private void chkLimitNowOrderBookDetectionBuy_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.LimitNowOrderBookBuy = chkLimitNowOrderBookDetectionBuy.Checked;
            SaveSettings();
        }

        private void chkLimitNowOrderBookDetectionSell_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.LimitNowOrderBookSell = chkLimitNowOrderBookDetectionSell.Checked;
            SaveSettings();
        }

        private void ConsoleText_TextChanged(object sender, EventArgs e)
        {
            ConsoleText.SelectionStart = ConsoleText.Text.Length;
            ConsoleText.ScrollToCaret();
        }

        private void btnLimitNowCheckPrice_Sell_Click(object sender, EventArgs e)
        {
            decimal OrderPrice = LimitNowGetOrderPrice("Sell");
            decimal StopLossDelta = ActiveInstrument.TickSize * LimitNowStopLossSellDelta;
            if (chkLimitNowStopLossSell.Checked)
            {
                Log("Sell Order Price:" + OrderPrice+":SL:"+(OrderPrice+StopLossDelta));
            }
            else
            {
                Log("Sell Order Price:" + OrderPrice);
            }
            OrderPrice = LimitNowGetOrderPrice("Sell",true);
            Log("Sell Order Price L2:" + OrderPrice);
        }

        private void btnLimitNowCheckPrice_Buy_Click(object sender, EventArgs e)
        {
            decimal OrderPrice = LimitNowGetOrderPrice("Buy");
            decimal StopLossDelta = ActiveInstrument.TickSize * LimitNowStopLossBuyDelta;
            if (chkLimitNowStopLossBuy.Checked)
            {
                Log("Buy Order Price:" + OrderPrice + ":SL:" + (OrderPrice - StopLossDelta));
            }
            else
            {
                Log("Buy Order Price:" + OrderPrice);
            }
            OrderPrice = LimitNowGetOrderPrice("Buy", true);
            Log("Buy Order Price L2:" + OrderPrice);
        }

        private void nudLimitNowBuyLevel_ValueChanged(object sender, EventArgs e)
        {
            LimitNowBuyLevel = (int)nudLimitNowBuyLevel.Value;
            Properties.Settings.Default.LimitNowBuyLevel = LimitNowBuyLevel;
            SaveSettings();
        }

        private void nudLimitNowSellLevel_ValueChanged(object sender, EventArgs e)
        {
            LimitNowSellLevel = (int)nudLimitNowSellLevel.Value;
            Properties.Settings.Default.LimitNowSellLevel = LimitNowSellLevel;
            SaveSettings();
        }

        private void metroLabel9_Click(object sender, EventArgs e)
        {

        }

        private void btnClearLog_Click(object sender, EventArgs e)
        {
            LogLines.Clear();
            ConsoleText.Lines = null;
        }

        private void lblNetworkAndVersion_Click(object sender, EventArgs e)
        {

        }

        private void metroButton1_Click(object sender, EventArgs e)
        {
            ws_user.Send("ping");
        }
    }

    public class Alert
    {
        public string Symbol { get; set; }
        public string Side { get; set; }
        public decimal Price { get; set; }
        public bool Triggered { get; set; }
    }
}
