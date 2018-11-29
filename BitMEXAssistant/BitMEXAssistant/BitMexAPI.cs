//using ServiceStack.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

#pragma warning disable CS1634, CS0168, CS0162
namespace BitMEX
{
    public class OrderBookItem
    {
        public string Symbol { get; set; }
        public int Level { get; set; }
        public int BidSize { get; set; }
        public decimal BidPrice { get; set; }
        public int AskSize { get; set; }
        public decimal AskPrice { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class BitMEXApi
    {
        private string domain = "https://www.bitmex.com";
        private string testdomain = "https://testnet.bitmex.com";
        private string apiKey;
        private string apiSecret;
        private int rateLimit;
        List<string> errors = new List<string>();

        public int MaxCallsLimit = 0;
        public int CallsRemaining = 0;
        public EventHandler UpdateApiRemainingHandler = null;

        public BitMEXApi(string bitmexKey = "", string bitmexSecret = "", bool RealNetwork = true, int rateLimit = 5000)
        {
            this.apiKey = bitmexKey;
            this.apiSecret = bitmexSecret;
            this.rateLimit = rateLimit;
            if(!RealNetwork)
            {
                this.domain = testdomain;
            }
        }

        #region API Connector - Don't touch
        private string BuildQueryData(Dictionary<string, string> param)
        {
            if (param == null)
                return "";

            StringBuilder b = new StringBuilder();
            foreach (var item in param)
                b.Append(string.Format("&{0}={1}", item.Key, WebUtility.UrlEncode(item.Value)));

            try { return b.ToString().Substring(1); }
            catch (Exception) { return ""; }
        }

        private string BuildJSON(Dictionary<string, string> param)
        {
            if (param == null)
                return "";

            var entries = new List<string>();
            foreach (var item in param)
                entries.Add(string.Format("\"{0}\":\"{1}\"", item.Key, item.Value));

            return "{" + string.Join(",", entries) + "}";
        }

        public static string ByteArrayToString(byte[] ba)
        {
            StringBuilder hex = new StringBuilder(ba.Length * 2);
            foreach (byte b in ba)
                hex.AppendFormat("{0:x2}", b);
            return hex.ToString();
        }

        private long GetNonce()
        {
            DateTime yearBegin = new DateTime(2018, 7, 17);
            return DateTime.UtcNow.Ticks - yearBegin.Ticks;
        }

        public byte[] hmacsha256(byte[] keyByte, byte[] messageBytes)
        {
            using (var hash = new HMACSHA256(keyByte))
            {
                return hash.ComputeHash(messageBytes);
            }
        }

        public string GetExpiresArg()
        {
            long timestamp = (long)((DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds);

            string expires = (timestamp + 60).ToString();

            return (expires);
        }

        public string GetWebSocketSignatureString(string APISecret, string APIExpires)
        {
            byte[] signatureBytes = hmacsha256(Encoding.UTF8.GetBytes(APISecret), Encoding.UTF8.GetBytes("GET/realtime" + APIExpires));
            string signatureString = ByteArrayToString(signatureBytes);
            return signatureString;
        }

        private string Query(string method, string function, Dictionary<string, string> param = null, bool auth = false, bool json = false)
        {
            string paramData = json ? BuildJSON(param) : BuildQueryData(param);
            string url = "/api/v1" + function + ((method == "GET" && paramData != "") ? "?" + paramData : "");
            string postData = (method != "GET") ? paramData : "";

            HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create(domain + url);
            webRequest.Method = method;

            if (auth)
            {
                //string nonce = GetNonce().ToString();
                //string message = method + url + nonce + postData;
                string expires = GetExpiresArg();
                string message = method + url + expires + postData;
                byte[] signatureBytes = hmacsha256(Encoding.UTF8.GetBytes(apiSecret), Encoding.UTF8.GetBytes(message));
                string signatureString = ByteArrayToString(signatureBytes);

                //webRequest.Headers.Add("api-nonce", nonce);
                webRequest.Headers.Add("api-expires", expires);
                webRequest.Headers.Add("api-key", apiKey);
                webRequest.Headers.Add("api-signature", signatureString);
            }

            try
            {
                if (postData != "")
                {
                    webRequest.ContentType = json ? "application/json" : "application/x-www-form-urlencoded";
                    var data = Encoding.UTF8.GetBytes(postData);
                    using (var stream = webRequest.GetRequestStream())
                    {
                        stream.Write(data, 0, data.Length);
                    }
                }

                using (WebResponse webResponse = webRequest.GetResponse())
                {
                    using (Stream str = webResponse.GetResponseStream())
                    {
                        //Console.WriteLine("Headers:" + webResponse.Headers.ToString());
                        int.TryParse(webResponse.Headers.Get("x-ratelimit-limit"), out MaxCallsLimit);
                        int.TryParse(webResponse.Headers.Get("x-ratelimit-remaining"), out CallsRemaining);
                        if (UpdateApiRemainingHandler!=null)
                        {
                            UpdateApiRemainingHandler(this, EventArgs.Empty);
                        }
                        using (StreamReader sr = new StreamReader(str))
                        {
                            return sr.ReadToEnd();
                        }
                    }
                }
            }
            catch (WebException wex)
            {
                using (HttpWebResponse response = (HttpWebResponse)wex.Response)
                {
                    if (response == null)
                        throw;

                    using (Stream str = response.GetResponseStream())
                    {
                        using (StreamReader sr = new StreamReader(str))
                        {
                            return sr.ReadToEnd();
                        }
                    }
                }
            }
        }
        #endregion

        #region Examples from BitMex
        //public List<OrderBookItem> GetOrderBook(string symbol, int depth)
        //{
        //    var param = new Dictionary<string, string>();
        //    param["symbol"] = symbol;
        //    param["depth"] = depth.ToString();
        //    string res = Query("GET", "/orderBook", param);
        //    return JsonSerializer.DeserializeFromString<List<OrderBookItem>>(res);
        //}

        //public string GetOrders(string Symbol)
        //{
        //    var param = new Dictionary<string, string>();
        //    param["symbol"] = Symbol;
        //    //param["filter"] = "{\"open\":true}";
        //    //param["columns"] = "";
        //    //param["count"] = 100.ToString();
        //    //param["start"] = 0.ToString();
        //    //param["reverse"] = false.ToString();
        //    //param["startTime"] = "";
        //    //param["endTime"] = "";
        //    return Query("GET", "/order", param, true);
        //}

        //public string PostOrders()
        //{
        //    var param = new Dictionary<string, string>();
        //    param["symbol"] = "XBTUSD";
        //    param["side"] = "Buy";
        //    param["orderQty"] = "1";
        //    param["ordType"] = "Market";
        //    return Query("POST", "/order", param, true);
        //}

        //public string DeleteOrders()
        //{
        //    var param = new Dictionary<string, string>();
        //    param["orderID"] = "de709f12-2f24-9a36-b047-ab0ff090f0bb";
        //    param["text"] = "cancel order by ID";
        //    return Query("DELETE", "/order", param, true, true);
        //}
        #endregion

        #region Our Calls

        public List<Order> MarketClosePosition(string Symbol, string Side)
        {
            var param = new Dictionary<string, string>();
            param["symbol"] = Symbol;
            param["side"] = Side;
            param["orderQty"] = "0";
            param["ordType"] = "Market";
            param["execInst"] = "Close";

            string res = Query("POST", "/order", param, true);
            int RetryAttemptCount = 0;
            int MaxRetries = RetryAttempts(res);
            while (res.Contains("error") && RetryAttemptCount < MaxRetries)
            {
                errors.Add(res);
                Thread.Sleep(BitMEXAssistant.Properties.Settings.Default.RetryAttemptWaitTime); // Force app to wait 500ms
                res = Query("POST", "/order", param, true);
                RetryAttemptCount++;
                if (RetryAttemptCount == MaxRetries)
                {
                    errors.Add("Max rety attempts of " + MaxRetries.ToString() + " reached.");
                    break;
                }
            }
            try
            {
                List<Order> Result = new List<Order>();
                Result.Add(JsonConvert.DeserializeObject<Order>(res));
                return Result;
            }
            catch (Exception ex)
            {
                return new List<Order>();
            }
        }
        #region Ordering
        public List<Order> MarketOrder(string Symbol, string Side, int Quantity, bool ReduceOnly = false)
        {
            var param = new Dictionary<string, string>();
            param["symbol"] = Symbol;
            param["side"] = Side;
            param["orderQty"] = Quantity.ToString();
            param["ordType"] = "Market";
            if(ReduceOnly)
            {
                param["execInst"] = "ReduceOnly";
            }
            string res = Query("POST", "/order", param, true);
            int RetryAttemptCount = 0;
            int MaxRetries = RetryAttempts(res);
            while (res.Contains("error") && RetryAttemptCount < MaxRetries)
            {
                errors.Add(res);
                Thread.Sleep(BitMEXAssistant.Properties.Settings.Default.RetryAttemptWaitTime); // Force app to wait 500ms
                res = Query("POST", "/order", param, true);
                RetryAttemptCount++;
                if (RetryAttemptCount == MaxRetries)
                {
                    errors.Add("Max rety attempts of " + MaxRetries.ToString() + " reached.");
                    break;
                }
            }
            try
            {
                List<Order> Result = new List<Order>();
                Result.Add(JsonConvert.DeserializeObject<Order>(res));
                return Result;
            }
            catch (Exception ex)
            {
                return new List<Order>();
            }
            //return res;
        }

        public List<Order> LimitOrder(string Symbol, string Side, int Quantity, decimal Price, bool ReduceOnly = false, bool PostOnly = false, bool Hidden = false)
        {
            var param = new Dictionary<string, string>();
            param["symbol"] = Symbol;
            param["side"] = Side;
            param["orderQty"] = Quantity.ToString();
            param["ordType"] = "Limit";
            param["price"] = Price.ToString().Replace(",",".");
            if (ReduceOnly && !PostOnly)
            {
                param["execInst"] = "ReduceOnly";
            }
            else if(!ReduceOnly && PostOnly)
            {
                param["execInst"] = "ParticipateDoNotInitiate";
            }
            else if(ReduceOnly && PostOnly)
            {
                param["execInst"] = "ReduceOnly,ParticipateDoNotInitiate";
            }
            if(Hidden)
            {
                param["displayQty"] = "0";
            }

            param["clOrdID"] = Guid.NewGuid().ToString();

            string res = Query("POST", "/order", param, true);
            int RetryAttemptCount = 0;
            int MaxRetries = RetryAttempts(res);
            while (res.Contains("error") && RetryAttemptCount < MaxRetries)
            {
                errors.Add(res);
                Thread.Sleep(BitMEXAssistant.Properties.Settings.Default.RetryAttemptWaitTime); // Force app to wait 500ms
                res = Query("POST", "/order", param, true);
                RetryAttemptCount++;
                if (RetryAttemptCount == MaxRetries)
                {
                    errors.Add("Max rety attempts of " + MaxRetries.ToString() + " reached.");
                    break;
                }
            }
            try
            {
                List<Order> Result = new List<Order>();
                Result.Add(JsonConvert.DeserializeObject<Order>(res));
                return Result;
            }
            catch (Exception ex)
            {
                return new List<Order>();
            }
            //return res;
        }

        public List<Order> LimitOrderSafety(string Symbol, string Side, int Quantity, decimal Price, bool ReduceOnly = false, bool PostOnly = false, bool Hidden = false)
        {
#if TRUE
            Order limitOrder = new Order();
            limitOrder.Symbol = Symbol;
            limitOrder.Side = Side;
            limitOrder.OrderQty = Quantity;
            limitOrder.OrdType = "Limit";
            limitOrder.Price = Price;
            if (ReduceOnly && !PostOnly)
            {
                limitOrder.ExecInst = "ReduceOnly";
            }
            else if (!ReduceOnly && PostOnly)
            {
                limitOrder.ExecInst = "ParticipateDoNotInitiate";
            }
            else if (ReduceOnly && PostOnly)
            {
                limitOrder.ExecInst = "ReduceOnly,ParticipateDoNotInitiate";
            }
            if (Hidden)
            {
                limitOrder.ExecInst = "0";
            }
            limitOrder.ContingencyType = "OneTriggersTheOther";


            limitOrder.ClOrdID = Guid.NewGuid().ToString();
            limitOrder.ClOrdLinkID = Guid.NewGuid().ToString();
            //string order = JsonConvert.SerializeObject(limitOrder);
            // now we create the OTO order
            Order OTOOrder = new Order();
            OTOOrder.Symbol = Symbol;
            OTOOrder.Price = Price;
            if (Side == "Buy")
            {
                OTOOrder.Side = "Sell";
                //OTOOrder.Price -= 5;
            }
            else
            {
                OTOOrder.Side = "Buy";
                //OTOOrder.Price += 5;
            }
            OTOOrder.OrderQty = Quantity;
            OTOOrder.OrdType = "Limit";
            if (ReduceOnly && !PostOnly)
            {
                OTOOrder.ExecInst = "ReduceOnly";
            }
            else if (!ReduceOnly && PostOnly)
            {
                OTOOrder.ExecInst = "ParticipateDoNotInitiate";
            }
            else if (ReduceOnly && PostOnly)
            {
                OTOOrder.ExecInst = "ReduceOnly,ParticipateDoNotInitiate";
            }
            if (Hidden)
            {
                OTOOrder.ExecInst = "0";
            }
            OTOOrder.ClOrdID = Guid.NewGuid().ToString();
            OTOOrder.ClOrdLinkID = limitOrder.ClOrdLinkID;
            // do a close order
            /*
            OTOOrder.OrdType = "StopLimit";
            OTOOrder.StopPx = OTOOrder.Price; // this is the trigger price
            OTOOrder.ExecInst = "Close";
            */
            Console.WriteLine("Adding Orders");

            List<Order> orders = new List<Order>();
            orders.Add(limitOrder);
            orders.Add(OTOOrder);
            Console.WriteLine("Serializing the list of orders");
            string orderlist = JsonConvert.SerializeObject(orders);
            Console.WriteLine(orderlist);
            Console.WriteLine("Doing bulk");
            string res = BulkOrder(orderlist);
            Console.WriteLine("Bulk submitted");
            Console.WriteLine(res);
            try
            {
                List<Order> Result = new List<Order>();
                Console.WriteLine("Deserial Bulk submit result");
                Result = (JsonConvert.DeserializeObject<List<Order>>(res));
                Console.WriteLine("Bulk submit result done deserialing");

                return Result;
            }
            catch (Exception ex)
            {
                return new List<Order>();
            }
#else
            var param = new Dictionary<string, string>();
            param["symbol"] = Symbol;
            param["side"] = Side;
            param["orderQty"] = Quantity.ToString();
            param["ordType"] = "Limit";
            param["price"] = Price.ToString().Replace(",", ".");
            if (ReduceOnly && !PostOnly)
            {
                param["execInst"] = "ReduceOnly";
            }
            else if (!ReduceOnly && PostOnly)
            {
                param["execInst"] = "ParticipateDoNotInitiate";
            }
            else if (ReduceOnly && PostOnly)
            {
                param["execInst"] = "ReduceOnly,ParticipateDoNotInitiate";
            }
            if (Hidden)
            {
                param["displayQty"] = "0";
            }

            param["clOrdID"] = Guid.NewGuid().ToString();
            string res = Query("POST", "/order", param, true);
            int RetryAttemptCount = 0;
            int MaxRetries = RetryAttempts(res);
            while (res.Contains("error") && RetryAttemptCount < MaxRetries)
            {
                errors.Add(res);
                Thread.Sleep(BitMEXAssistant.Properties.Settings.Default.RetryAttemptWaitTime); // Force app to wait 500ms
                res = Query("POST", "/order", param, true);
                RetryAttemptCount++;
                if (RetryAttemptCount == MaxRetries)
                {
                    errors.Add("Max rety attempts of " + MaxRetries.ToString() + " reached.");
                    break;
                }
            }
            
            try
            {
                List<Order> Result = new List<Order>();
                Result.Add(JsonConvert.DeserializeObject<Order>(res));
                return Result;
            }
            catch (Exception ex)
            {
                return new List<Order>();
            }
            //return res;
#endif

            return new List<Order>();
        }

        public List<Order> LimitNowOrder(string Symbol, string Side, int Quantity, decimal Price, bool ReduceOnly = false, bool PostOnly = false, bool Hidden = false)
        {
            var param = new Dictionary<string, string>();
            param["symbol"] = Symbol;
            param["side"] = Side;
            param["orderQty"] = Quantity.ToString();
            param["ordType"] = "Limit";
            param["price"] = Price.ToString().Replace(",",".");
            if (ReduceOnly && !PostOnly)
            {
                param["execInst"] = "ReduceOnly";
            }
            else if (!ReduceOnly && PostOnly)
            {
                param["execInst"] = "ParticipateDoNotInitiate";
            }
            else if (ReduceOnly && PostOnly)
            {
                param["execInst"] = "ReduceOnly,ParticipateDoNotInitiate";
            }
            if (Hidden)
            {
                param["displayQty"] = "0";
            }
            /*
            param["contingencyType"] = "OneTriggersTheOther";
            param["clOrdLinkID"] = Guid.NewGuid().ToString();
            */
            param["clOrdID"] = Guid.NewGuid().ToString();
            string res = "";
            try
            {
                Console.WriteLine("Doing query");
                res = Query("POST", "/order", param, true);
            }
            catch (Exception ex)
            {

            }
            int RetryAttemptCount = 0;
            int MaxRetries = RetryAttempts(res);
            while (res.Contains("error") && RetryAttemptCount < MaxRetries)
            {
                errors.Add(res);
                Thread.Sleep(BitMEXAssistant.Properties.Settings.Default.RetryAttemptWaitTime); // Force app to wait 500ms
                res = Query("POST", "/order", param, true);
                RetryAttemptCount++;
                if (RetryAttemptCount == MaxRetries)
                {
                    errors.Add("Max rety attempts of " + MaxRetries.ToString() + " reached.");
                    break;
                }
            }

            try
            {
                Console.WriteLine("Parsing result:"+res);
                List<Order> Result = new List<Order>();
                Result.Add(JsonConvert.DeserializeObject<Order>(res));
                return Result;
            }
            catch (Exception ex)
            {
                return new List<Order>();
            }
        }

        public List<Order> LimitNowOrderSafety(string Symbol, string Side, int Quantity, decimal Price, decimal StopLossDelta, decimal TakeProfitDelta, decimal TickSize, bool UseMarketStopLoss, bool ReduceOnly = false, bool PostOnly = false, bool Hidden = false)
        {
#if TRUE
            //Console.WriteLine("LimitNowOrderSafety");
            Order limitOrder = new Order();
            limitOrder.Symbol = Symbol;
            limitOrder.Side = Side;
            limitOrder.OrderQty = Quantity;
            limitOrder.OrdType = "Limit";
            limitOrder.Price = Price;
            if (ReduceOnly && !PostOnly)
            {
                limitOrder.ExecInst = "ReduceOnly";
            }
            else if (!ReduceOnly && PostOnly)
            {
                limitOrder.ExecInst = "ParticipateDoNotInitiate";
            }
            else if (ReduceOnly && PostOnly)
            {
                limitOrder.ExecInst = "ReduceOnly,ParticipateDoNotInitiate";
            }
            if (Hidden)
            {
                limitOrder.ExecInst = "0";
            }
            limitOrder.ContingencyType = "OneTriggersTheOther";
            limitOrder.ClOrdID = Guid.NewGuid().ToString();

            limitOrder.ClOrdLinkID = Guid.NewGuid().ToString();

            List<Order> orders = new List<Order>();

            orders.Add(limitOrder);
            //string order = JsonConvert.SerializeObject(limitOrder);
            // now we create the OTO order
#if LIMITSTOPS
            if (StopLossDelta != 0m)
            {
                Order StopLossOrder = new Order();
                StopLossOrder.Symbol = Symbol;
                StopLossOrder.Price = Price;
                StopLossOrder.ContingencyType = "OneCancelsTheOther";
                StopLossOrder.ContingencyType = "";
                StopLossOrder.ExecInst += "ParticipateDoNotInitiate,Close,LastPrice";
                StopLossOrder.ClOrdID = Guid.NewGuid().ToString();
                StopLossOrder.ClOrdLinkID = limitOrder.ClOrdLinkID;

                StopLossOrder.OrderQty = Quantity;
                StopLossOrder.OrdType = "StopLimit";
                if (Side == "Buy")
                {
                    StopLossOrder.Side = "Sell";
                    StopLossOrder.Price = Price - StopLossDelta;
                    StopLossOrder.StopPx = Price;
                }
                else
                {
                    StopLossOrder.Side = "Buy";
                    StopLossOrder.Price = Price + StopLossDelta;
                    StopLossOrder.StopPx = Price;
                }
                orders.Add(StopLossOrder);
            }
            if (TakeProfitDelta != 0m)
            {
                Order TakeProfitOrder = new Order();
                TakeProfitOrder.Symbol = Symbol;
                TakeProfitOrder.Price = Price;
                TakeProfitOrder.ContingencyType = "OneCancelsTheOther";
                TakeProfitOrder.ContingencyType = "";
                TakeProfitOrder.ExecInst += "ParticipateDoNotInitiate";
                TakeProfitOrder.ClOrdID = Guid.NewGuid().ToString();
                TakeProfitOrder.ClOrdLinkID = limitOrder.ClOrdLinkID;

                TakeProfitOrder.OrderQty = Quantity;
                TakeProfitOrder.OrdType = "Limit";
                if (Side == "Buy")
                {
                    TakeProfitOrder.Side = "Sell";
                    TakeProfitOrder.Price = Price + TakeProfitDelta;
                    //TakeProfitOrder.StopPx = Price;
                }
                else
                {
                    TakeProfitOrder.Side = "Buy";
                    TakeProfitOrder.Price = Price - TakeProfitDelta;
                    //TakeProfitOrder.StopPx = Price;
                }
                orders.Add(TakeProfitOrder);
            }
#else
            if (StopLossDelta != 0m)
            {
                Order StopLossOrder = new Order();
                StopLossOrder.Symbol = Symbol;
                StopLossOrder.Price = Price;
                StopLossOrder.ContingencyType = "OneCancelsTheOther";
                //StopLossOrder.ContingencyType = "";
                if (!UseMarketStopLoss)
                    StopLossOrder.OrdType = "StopLimit"; // limit stoploss
                else
                    StopLossOrder.OrdType = "Stop"; // market stoploss
                if (Side == "Buy")
                {
                    StopLossOrder.Side = "Sell";
                    if (StopLossOrder.OrdType == "StopLimit")
                    {
                        StopLossOrder.Price = Price - StopLossDelta;
                        StopLossOrder.StopPx = StopLossOrder.Price;
                    }
                    else if (StopLossOrder.OrdType == "Stop")
                    {
                        StopLossOrder.Price = null;
                        StopLossOrder.StopPx = Price - StopLossDelta;
                    }
                }
                else
                {
                    StopLossOrder.Side = "Buy";
                    if (StopLossOrder.OrdType == "StopLimit")
                    {
                        StopLossOrder.Price = Price + StopLossDelta;
                        StopLossOrder.StopPx = StopLossOrder.Price;
                    }
                    else if (StopLossOrder.OrdType == "Stop")
                    {
                        StopLossOrder.Price = null;
                        StopLossOrder.StopPx = Price + StopLossDelta;
                    }
                }
                StopLossOrder.OrderQty = Quantity;
                StopLossOrder.ExecInst = "LastPrice";
                Console.WriteLine("StopLoss ExecInst:" + StopLossOrder.ExecInst);
                StopLossOrder.ClOrdID = Guid.NewGuid().ToString();
                StopLossOrder.ClOrdLinkID = limitOrder.ClOrdLinkID;
                orders.Add(StopLossOrder);
            }
            if (TakeProfitDelta != 0m)
            {
                Order TakeProfitOrder = new Order();
                TakeProfitOrder.Symbol = Symbol;
                TakeProfitOrder.Price = Price;
                TakeProfitOrder.ContingencyType = "OneCancelsTheOther";
                //TakeProfitOrder.ContingencyType = "";
                TakeProfitOrder.OrdType = "Limit";
                //TakeProfitOrder.OrdType = "LimitIfTouched"; // ok
                //TakeProfitOrder.OrdType = "MarketIfTouched"; // seems not good.. if its too close can result in a loss because it triggered and then the market when against
                if (Side == "Buy")
                {
                    TakeProfitOrder.Side = "Sell";
                    TakeProfitOrder.Price = Price + TakeProfitDelta;
                    if (TakeProfitOrder.OrdType == "Limit")
                    {
                        TakeProfitOrder.Price = Price + TakeProfitDelta;
                    }
                    else if (TakeProfitOrder.OrdType == "LimitIfTouched")
                    {
                        TakeProfitOrder.Price = Price + TakeProfitDelta;
                        TakeProfitOrder.StopPx = Price + TakeProfitDelta;
                    }
                    else if (TakeProfitOrder.OrdType == "MarketIfTouched")
                    {
                        TakeProfitOrder.Price = null;
                        TakeProfitOrder.StopPx = Price + TakeProfitDelta;
                    }
/*
                    if (TakeProfitOrder.OrdType.Contains("LimitIfTouched"))
#if TRIGGERED_STOPS
#if PRICE_TRIGGER
                        TakeProfitOrder.StopPx = Price;
#else
                        TakeProfitOrder.StopPx = Price + TickSize;
#endif
#else
                        TakeProfitOrder.StopPx = TakeProfitOrder.Price - TickSize;
#endif
*/
                }
                else
                {
                    TakeProfitOrder.Side = "Buy";
                    TakeProfitOrder.Price = Price - TakeProfitDelta;
                    if (TakeProfitOrder.OrdType == "Limit")
                    {
                        TakeProfitOrder.Price = Price - TakeProfitDelta;
                    }
                    else if (TakeProfitOrder.OrdType == "LimitIfTouched")
                    {
                        TakeProfitOrder.Price = Price - TakeProfitDelta;
                        TakeProfitOrder.StopPx = Price - TakeProfitDelta;
                    }
                    else if (TakeProfitOrder.OrdType == "MarketIfTouched")
                    {
                        TakeProfitOrder.Price = null;
                        TakeProfitOrder.StopPx = Price - TakeProfitDelta;
                    }
                    /*
                    #if TRIGGERED_STOPS
                    #if PRICE_TRIGGER
                                        TakeProfitOrder.StopPx = Price;
                    #else
                                        TakeProfitOrder.StopPx = Price - TickSize;
                    #endif
                    #else
                                        TakeProfitOrder.StopPx = TakeProfitOrder.Price + TickSize;
                    #endif
                    */
                }
                TakeProfitOrder.OrderQty = Quantity;
                if (TakeProfitOrder.OrdType=="LimitIfTouched" || TakeProfitOrder.OrdType == "MarketIfTouched")
                    TakeProfitOrder.ExecInst = "LastPrice";
                TakeProfitOrder.ClOrdID = Guid.NewGuid().ToString();
                TakeProfitOrder.ClOrdLinkID = limitOrder.ClOrdLinkID;
                orders.Add(TakeProfitOrder);
            }
#endif

            string orderlist = JsonConvert.SerializeObject(orders);
            string res = BulkOrder(orderlist);
            Console.WriteLine("Bulk Order return:"+res);
            try
            {
                List<Order> Result = new List<Order>();
                Result = (JsonConvert.DeserializeObject<List<Order>>(res));
                return Result;
            }
            catch (Exception ex)
            {
                return new List<Order>();
            }

#else
                    var param = new Dictionary<string, string>();
            param["symbol"] = Symbol;
            param["side"] = Side;
            param["orderQty"] = Quantity.ToString();
            param["ordType"] = "Limit";
            param["price"] = Price.ToString().Replace(",", ".");
            if (ReduceOnly && !PostOnly)
            {
                param["execInst"] = "ReduceOnly";
            }
            else if (!ReduceOnly && PostOnly)
            {
                param["execInst"] = "ParticipateDoNotInitiate";
            }
            else if (ReduceOnly && PostOnly)
            {
                param["execInst"] = "ReduceOnly,ParticipateDoNotInitiate";
            }
            if (Hidden)
            {
                param["displayQty"] = "0";
            }


            string res = Query("POST", "/order", param, true);
            int RetryAttemptCount = 0;
            int MaxRetries = RetryAttempts(res);
            while (res.Contains("error") && RetryAttemptCount < MaxRetries)
            {
                errors.Add(res);
                Thread.Sleep(BitMEXAssistant.Properties.Settings.Default.RetryAttemptWaitTime); // Force app to wait 500ms
                res = Query("POST", "/order", param, true);
                RetryAttemptCount++;
                if (RetryAttemptCount == MaxRetries)
                {
                    errors.Add("Max rety attempts of " + MaxRetries.ToString() + " reached.");
                    break;
                }
            }

            try
            {
                List<Order> Result = new List<Order>();
                Result.Add(JsonConvert.DeserializeObject<Order>(res));
                return Result;
            }
            catch (Exception ex)
            {
                return new List<Order>();
            }
#endif
            return new List<Order>();
        }

        public List<Order> LimitNowOrderBreakout(string Symbol, string Side, int Quantity, decimal Price, bool ReduceOnly = false, bool PostOnly = false, bool Hidden = false)
        {
#if TRUE
            //Console.WriteLine("LimitNowOrderSafety");
            Order limitOrder = new Order();
            limitOrder.Symbol = Symbol;
            limitOrder.Side = Side;
            limitOrder.OrderQty = Quantity;
            limitOrder.OrdType = "Limit";
            limitOrder.Price = Price;
            if (ReduceOnly && !PostOnly)
            {
                limitOrder.ExecInst = "ReduceOnly";
            }
            else if (!ReduceOnly && PostOnly)
            {
                limitOrder.ExecInst = "ParticipateDoNotInitiate";
            }
            else if (ReduceOnly && PostOnly)
            {
                limitOrder.ExecInst = "ReduceOnly,ParticipateDoNotInitiate";
            }
            if (Hidden)
            {
                limitOrder.ExecInst = "0";
            }
            limitOrder.ContingencyType = "OneTriggersTheOther";

            limitOrder.ClOrdLinkID = Guid.NewGuid().ToString();
            //string order = JsonConvert.SerializeObject(limitOrder);
            // now we create the OTO order
            Order OTOOrder = new Order();
            OTOOrder.Symbol = Symbol;
            OTOOrder.Price = Price;
            if (Side == "Buy")
            {
                OTOOrder.Side = "Sell";
            }
            else
            {
                OTOOrder.Side = "Buy";
            }
            OTOOrder.OrderQty = Quantity;
            OTOOrder.OrdType = "Limit";
            if (ReduceOnly && !PostOnly)
            {
                OTOOrder.ExecInst = "ReduceOnly";
            }
            else if (!ReduceOnly && PostOnly)
            {
                OTOOrder.ExecInst = "ParticipateDoNotInitiate";
            }
            else if (ReduceOnly && PostOnly)
            {
                OTOOrder.ExecInst = "ReduceOnly,ParticipateDoNotInitiate";
            }
            if (Hidden)
            {
                OTOOrder.ExecInst = "0";
            }
            //OTOOrder.ClOrdID = Guid.NewGuid().ToString();
            OTOOrder.ClOrdLinkID = limitOrder.ClOrdLinkID;

            List<Order> orders = new List<Order>();

            orders.Add(limitOrder);
            orders.Add(OTOOrder);

            string orderlist = JsonConvert.SerializeObject(orders);
            string res = BulkOrder(orderlist);
            try
            {
                List<Order> Result = new List<Order>();
                Result = (JsonConvert.DeserializeObject<List<Order>>(res));
                return Result;
            }
            catch (Exception ex)
            {
                return new List<Order>();
            }

#else
            var param = new Dictionary<string, string>();
            param["symbol"] = Symbol;
            param["side"] = Side;
            param["orderQty"] = Quantity.ToString();
            param["ordType"] = "Limit";
            param["price"] = Price.ToString().Replace(",", ".");
            if (ReduceOnly && !PostOnly)
            {
                param["execInst"] = "ReduceOnly";
            }
            else if (!ReduceOnly && PostOnly)
            {
                param["execInst"] = "ParticipateDoNotInitiate";
            }
            else if (ReduceOnly && PostOnly)
            {
                param["execInst"] = "ReduceOnly,ParticipateDoNotInitiate";
            }
            if (Hidden)
            {
                param["displayQty"] = "0";
            }


            string res = Query("POST", "/order", param, true);
            int RetryAttemptCount = 0;
            int MaxRetries = RetryAttempts(res);
            while (res.Contains("error") && RetryAttemptCount < MaxRetries)
            {
                errors.Add(res);
                Thread.Sleep(BitMEXAssistant.Properties.Settings.Default.RetryAttemptWaitTime); // Force app to wait 500ms
                res = Query("POST", "/order", param, true);
                RetryAttemptCount++;
                if (RetryAttemptCount == MaxRetries)
                {
                    errors.Add("Max rety attempts of " + MaxRetries.ToString() + " reached.");
                    break;
                }
            }

            try
            {
                List<Order> Result = new List<Order>();
                Result.Add(JsonConvert.DeserializeObject<Order>(res));
                return Result;
            }
            catch (Exception ex)
            {
                return new List<Order>();
            }
#endif
            return new List<Order>();
        }

        public List<Order> LimitNowAmendOrder(string OrderId, decimal? Price = null, int? OrderQty = null)
        {
            var param = new Dictionary<string, string>();
            param["orderID"] = OrderId;
            if(Price != null)
            {
                param["price"] = Price.ToString().Replace(",",".");
            }
            if(OrderQty != null)
            {
                param["orderQty"] = OrderQty.ToString();
            }

            string res = Query("PUT", "/order", param, true);
            Console.WriteLine("Amended:" + res);
            try
            {
                List<Order> Result = new List<Order>();
                if(!res.Contains("error"))
                {
                    Result.Add(JsonConvert.DeserializeObject<Order>(res));
                    return Result;
                }
                else
                {
                    return new List<Order>();
                }
                
            }
            catch (Exception ex)
            {
                return new List<Order>();
            }
        }

        public List<Order> TrailingStopAmendOrder(string OrderId, decimal? Price = null, int? OrderQty = null)
        {
            var param = new Dictionary<string, string>();
            param["orderID"] = OrderId;
            if (Price != null)
            {
                param["price"] = Price.ToString().Replace(",",".");
            }
            if (OrderQty != null)
            {
                param["orderQty"] = OrderQty.ToString();
            }

            string res = Query("PUT", "/order", param, true);

            try
            {
                List<Order> Result = new List<Order>();
                if (!res.Contains("error"))
                {
                    Result.Add(JsonConvert.DeserializeObject<Order>(res));
                    return Result;
                }
                else
                {
                    return new List<Order>();
                }

            }
            catch (Exception ex)
            {
                return new List<Order>();
            }
        }

        public List<Order> CancelOrder(string [] OrderId)
        {
            Console.WriteLine("Cancel Order");
            var param = new Dictionary<string, string>();
            string orders = JsonConvert.SerializeObject(OrderId);
            param["orderID"] = orders;
            
            string res = Query("DELETE", "/order", param, true);
            int RetryAttemptCount = 0;
            int MaxRetries = RetryAttempts(res);
            while (res.Contains("error") && RetryAttemptCount < MaxRetries)
            {
                errors.Add(res);
                Thread.Sleep(BitMEXAssistant.Properties.Settings.Default.RetryAttemptWaitTime); // Force app to wait 500ms
                res = Query("DELETE", "/order", param, true);
                RetryAttemptCount++;
                if (RetryAttemptCount == MaxRetries)
                {
                    errors.Add("Max rety attempts of " + MaxRetries.ToString() + " reached.");
                    break;
                }
            }

            try
            {
                List<Order> Result = new List<Order>();
                Result = (JsonConvert.DeserializeObject<List<Order>>(res));
                return Result;
            }
            catch (Exception ex)
            {
                return new List<Order>();
            }
        }

        public string ChangeMargin(string Symbol, decimal Margin)
        {
            var param = new Dictionary<string, string>();
            param["symbol"] = Symbol;
            param["leverage"] = Margin.ToString();



            string res = Query("POST", "/position/leverage", param, true);
            int RetryAttemptCount = 0;
            int MaxRetries = RetryAttempts(res);
            while (res.Contains("error") && RetryAttemptCount < MaxRetries)
            {
                errors.Add(res);
                Thread.Sleep(BitMEXAssistant.Properties.Settings.Default.RetryAttemptWaitTime); // Force app to wait 500ms
                res = Query("POST", "/order", param, true);
                RetryAttemptCount++;
                if (RetryAttemptCount == MaxRetries)
                {
                    errors.Add("Max rety attempts of " + MaxRetries.ToString() + " reached.");
                    break;
                }
            }
            return res;
        }

        public string CancelAllOpenOrders(string symbol, string Note = "")
        {
            var param = new Dictionary<string, string>();
            param["symbol"] = symbol;
            param["text"] = Note;
            string res = Query("DELETE", "/order/all", param, true, true);
            int RetryAttemptCount = 0;
            int MaxRetries = RetryAttempts(res);
            while (res.Contains("error") && RetryAttemptCount < MaxRetries)
            {
                errors.Add(res);
                Thread.Sleep(BitMEXAssistant.Properties.Settings.Default.RetryAttemptWaitTime); // Force app to wait 500ms
                res = Query("DELETE", "/order/all", param, true, true);
                RetryAttemptCount++;
                if (RetryAttemptCount == MaxRetries)
                {
                    errors.Add("Max rety attempts of " + MaxRetries.ToString() + " reached.");
                    break;
                }
            }
            return res;
        }

        public string BulkOrder(string Orders)
        {
            var param = new Dictionary<string, string>();
            param["orders"] = Orders;
            string res;
            res = Query("POST", "/order/bulk", param, true);
            int RetryAttemptCount = 0;
            int MaxRetries = RetryAttempts(res);
            while (res.Contains("error") && RetryAttemptCount < MaxRetries)
            {
                errors.Add(res);
                Thread.Sleep(BitMEXAssistant.Properties.Settings.Default.RetryAttemptWaitTime); // Force app to wait 500ms
                res = Query("POST", "/order/bulk", param, true);
                RetryAttemptCount++;
                if (RetryAttemptCount == MaxRetries)
                {
                    errors.Add("Max rety attempts of " + MaxRetries.ToString() + " reached.");
                    break;
                }
            }
            return res;
        }

        public string AmendBulkOrder(string Orders)
        {
            var param = new Dictionary<string, string>();
            param["orders"] = Orders;
            string res = Query("PUT", "/order/bulk", param, true);
            int RetryAttemptCount = 0;
            int MaxRetries = RetryAttempts(res);
            while (res.Contains("error") && RetryAttemptCount < MaxRetries)
            {
                errors.Add(res);
                Thread.Sleep(BitMEXAssistant.Properties.Settings.Default.RetryAttemptWaitTime); // Force app to wait 500ms
                res = Query("PUT", "/order/bulk", param, true);
                RetryAttemptCount++;
                if (RetryAttemptCount == MaxRetries)
                {
                    errors.Add("Max rety attempts of " + MaxRetries.ToString() + " reached.");
                    break;
                }
            }
            return res;
        }


#endregion


        public List<Instrument> GetAllInstruments()
        {
            string res = Query("GET", "/instrument?columns=symbol,tickSize&start=0&count=500");
            int RetryAttemptCount = 0;
            int MaxRetries = RetryAttempts(res);
            while (res.Contains("error") && RetryAttemptCount < MaxRetries)
            {
                errors.Add(res);
                Thread.Sleep(BitMEXAssistant.Properties.Settings.Default.RetryAttemptWaitTime); // Force app to wait 500ms
                res = Query("GET", "/instrument/active");
                RetryAttemptCount++;
                if (RetryAttemptCount == MaxRetries)
                {
                    errors.Add("Max rety attempts of " + MaxRetries.ToString() + " reached.");
                    break;
                }
            }
            try
            {
                return JsonConvert.DeserializeObject<List<Instrument>>(res);
            }
            catch (Exception ex)
            {
                
                return new List<Instrument>();
            }
        }

        public List<Instrument> GetActiveInstruments()
        {
            string res = Query("GET", "/instrument/active");
            int RetryAttemptCount = 0;
            int MaxRetries = RetryAttempts(res);
            while (res.Contains("error") && RetryAttemptCount < MaxRetries)
            {
                errors.Add(res);
                Thread.Sleep(BitMEXAssistant.Properties.Settings.Default.RetryAttemptWaitTime); // Force app to wait 500ms
                res = Query("GET", "/instrument/active");
                RetryAttemptCount++;
                if (RetryAttemptCount == MaxRetries)
                {
                    errors.Add("Max rety attempts of " + MaxRetries.ToString() + " reached.");
                    break;
                }
            }
            try
            {
                return JsonConvert.DeserializeObject<List<Instrument>>(res);
            }
            catch (Exception ex)
            {
                return new List<Instrument>();
            }
        }

        public List<Instrument> GetInstrument(string symbol)
        {
            var param = new Dictionary<string, string>();
            param["symbol"] = symbol;
            string res = Query("GET", "/instrument", param);
            int RetryAttemptCount = 0;
            int MaxRetries = RetryAttempts(res);
            while (res.Contains("error") && RetryAttemptCount < MaxRetries)
            {
                errors.Add(res);
                Thread.Sleep(BitMEXAssistant.Properties.Settings.Default.RetryAttemptWaitTime); // Force app to wait 500ms
                res = Query("GET", "/instrument", param);
                RetryAttemptCount++;
                if (RetryAttemptCount == MaxRetries)
                {
                    errors.Add("Max rety attempts of " + MaxRetries.ToString() + " reached.");
                    break;
                }
            }
            try
            {
                return JsonConvert.DeserializeObject<List<Instrument>>(res);
            }
            catch (Exception ex)
            {
                return new List<Instrument>();
            }
        }

        public List<Position> GetOpenPositions(string symbol)
        {
            var param = new Dictionary<string, string>();
            string res = Query("GET", "/position", param, true);
            int RetryAttemptCount = 0;
            int MaxRetries = RetryAttempts(res);
            while (res.Contains("error") && RetryAttemptCount < MaxRetries)
            {
                errors.Add(res);
                Thread.Sleep(BitMEXAssistant.Properties.Settings.Default.RetryAttemptWaitTime); // Force app to wait 500ms
                res = Query("GET", "/position", param, true);
                RetryAttemptCount++;
                if (RetryAttemptCount == MaxRetries)
                {
                    errors.Add("Max rety attempts of " + MaxRetries.ToString() + " reached.");
                    break;
                }
            }
            try
            {
                return JsonConvert.DeserializeObject<List<Position>>(res).Where(a => a.Symbol == symbol && a.IsOpen == true).OrderByDescending(a => a.TimeStamp).ToList();
            }
            catch (Exception ex)
            {
                return new List<Position>();
            }
        }

        public decimal GetCurrentPrice(string symbol)
        {
            var param = new Dictionary<string, string>();
            param["symbol"] = symbol;
            param["reverse"] = true.ToString();
            param["count"] = "1";
            string res = Query("GET", "/trade", param, true);
            if(res.Contains("error"))
            {
                return 0;
            }
            else
            {
                return JsonConvert.DeserializeObject<List<Trade>>(res).FirstOrDefault().Price;
            }
            
        }

        // Getting Account Balance
        public decimal GetAccountBalance()
        {
            var param = new Dictionary<string, string>();
            param["currency"] = "XBt";
            string res = Query("GET", "/user/margin", param, true);
            if (res.Contains("error"))
            {
                return -1;
            }
            else
            {
                return Convert.ToDecimal(JsonConvert.DeserializeObject<Margin>(res).UsefulBalance); // useful balance is the balance with full decimal places.
                // default wallet balance doesn't show the decimal places like it should.
            }

        }

        // Get API Key Permissions
        public bool GetAPIKeyPermissions()
        {
            string res = Query("GET", "/apiKey", null, true);
            if (res.Contains("error"))
            {
                return false;
            }
            else
            {
                JArray json = JArray.Parse(res);
                foreach (JObject api_key in json)
                {
                    string key = api_key.GetValue("id").ToString();
                    if (key == this.apiKey)
                    {
                        JToken permissions = api_key.GetValue("permissions");
                        foreach (JToken permission in permissions)
                        {
                            if (permission.ToString() == "order")
                            {
                                return true;
                            }
                        }
                    }
                }
                return false;
            }
        }

        public List<SimpleCandle> GetCandleHistory(string symbol, string size, DateTime Start = new DateTime())
        {
            var param = new Dictionary<string, string>();
            param["symbol"] = symbol;
            param["count"] = 500.ToString(); // 500 max
            param["reverse"] = "false";
            if (Start != new DateTime())
            {
                param["startTime"] = Start.ToString();
            }
            param["binSize"] = size;
            string res = Query("GET", "/trade/bucketed", param);
            int RetryAttemptCount = 0;
            int MaxRetries = RetryAttempts(res);
            while (res.Contains("error") && RetryAttemptCount < MaxRetries)
            {
                errors.Add(res);
                Thread.Sleep(500); // Force app to wait 500ms
                res = Query("GET", "/trade/bucketed", param);
                RetryAttemptCount++;
                if (RetryAttemptCount == MaxRetries)
                {
                    errors.Add("Max rety attempts of " + MaxRetries.ToString() + " reached.");
                    break;
                }
            }
            try
            {
                return JsonConvert.DeserializeObject<List<SimpleCandle>>(res).OrderBy(a => a.TimeStamp).ToList();
            }
            catch (Exception ex)
            {
                return new List<SimpleCandle>();
            }

        }

#endregion


        private int RetryAttempts(string res)
        {
            int att = 0;

            if (res.Contains("Unable to cancel order due to existing state"))
            {
                att = 0;
            }
            else if (res.Contains("The system is currently overloaded. Please try again later."))
            {
                if(BitMEXAssistant.Properties.Settings.Default.OverloadRetry)
                {
                    att = BitMEXAssistant.Properties.Settings.Default.OverloadRetryAttempts;
                }
                else
                {
                    att = 0;
                }
            }
            else if (res.Contains("error"))
            {
                att = 0;
            }

            return att;
        }



#region RateLimiter

        private long lastTicks = 0;
        private object thisLock = new object();

        private void RateLimit()
        {
            lock (thisLock)
            {
                long elapsedTicks = DateTime.Now.Ticks - lastTicks;
                var timespan = new TimeSpan(elapsedTicks);
                if (timespan.TotalMilliseconds < rateLimit)
                    Thread.Sleep(rateLimit - (int)timespan.TotalMilliseconds);
                lastTicks = DateTime.Now.Ticks;
            }
        }

#endregion RateLimiter
    }

    // Working Classes
    public class Margin // For account balance
    {
        public double? WalletBalance { get; set; }
        public double? AvailableMargin { get; set; }
        public double? UsefulBalance
        {
            get { return (WalletBalance / 100000000) ?? 0; }
        }
    }

    public class OrderBook
    {
        public string Side { get; set; }
        public decimal Price { get; set; }
        public int Size { get; set; }
    }

    public class Instrument
    {
        public string Symbol { get; set; }
        public decimal TickSize { get; set; }
        public double Volume24H { get; set; }
        public int DecimalPlacesInTickSize
        {
            get { return BitConverter.GetBytes(decimal.GetBits(TickSize)[3])[2]; }
        }
    }

    public class Candle
    {
        public DateTime TimeStamp { get; set; }
        public double? Open { get; set; }
        public double? Close { get; set; }
        public double? High { get; set; }
        public double? Low { get; set; }
        public double? Volume { get; set; }
        public int Trades { get; set; }
        public int PCC { get; set; }
        public double? MA1 { get; set; }
        public double? MA2 { get; set; }

        public double? PVT { get; set; } // NEW - for PVT

        public double? STOCHK { get; set; }
        public double? STOCHD { get; set; }

        public double? TypicalPrice
        {
            get { return ((High + Low + Close) / 3) ?? 0; } // 0 if null
        }//  For MFI
        public double? RawMoneyFlow
        {
            get { return (TypicalPrice * Volume) ?? 0; } // 0 if null
        }//  For MFI
        public double? MoneyFlowRatio { get; set; } //  For MFI
        public double? MoneyFlowChange { get; set; } //  For MFI // This gets set to the TypicalPrice of this candle, to the TypicalPrice of the previous candle
        public double? MFI { get; set; } //  For MFI

        public double? BBUpper { get; set; }
        public double? BBMiddle { get; set; }
        public double? BBLower { get; set; }
        public double? EMA1 { get; set; }
        public double? EMA2 { get; set; }
        public double? EMA3 { get; set; }
        public double? MACDLine { get; set; }
        public double? MACDSignalLine { get; set; }
        public double? MACDHistorgram { get; set; }
        public double? TR { get; set; }
        public double? ATR1 { get; set; }
        public double? ATR2 { get; set; }
        public double? GainOrLoss // For RSI
        {
            get { return (Close - Open) ?? 0; } // 0 if null
        }
        public double? RS { get; set; } // For RSI
        public double? RSI { get; set; } // For RSI
        public double? AVGGain { get; set; } // For RSI
        public double? AVGLoss { get; set; } // For RSI




        public void SetMoneyFlowChange(double? PreviousTypicalPrice) // NEW - For MFI
        {
            MoneyFlowChange = TypicalPrice - PreviousTypicalPrice;
        }

        public void SetTR(double? PreviousClose)
        {
            List<double?> TRs = new List<double?>();

            TRs.Add(High - Low);
            TRs.Add(Convert.ToDouble(Math.Abs(Convert.ToDecimal(High - PreviousClose))));
            TRs.Add(Convert.ToDouble(Math.Abs(Convert.ToDecimal(Low - PreviousClose))));

            TR = TRs.Max();
        }

        
    }

    public class Position
    {
        public DateTime TimeStamp { get; set; }
        public decimal? Leverage { get; set; }
        public int? CurrentQty { get; set; }
        public decimal? CurrentCost { get; set; }
        public bool IsOpen { get; set; }
        public decimal? MarkPrice { get; set; }
        public decimal? MarkValue { get; set; }
        public decimal? UnrealisedPnl { get; set; }
        public decimal? UnrealisedPnlPcnt { get; set; }
        public decimal? UnrealisedRoePcnt { get; set; }
        public decimal? AvgEntryPrice { get; set; }
        public decimal? BreakEvenPrice { get; set; }
        public decimal? LiquidationPrice { get; set; }
        public decimal? RealizedPnl { get; set; }
        public decimal? HighestPriceSinceOpen { get; set; }
        public decimal? LowestPriceSinceOpen { get; set; }
        public decimal? TrailingStopPrice { get; set; }

        public string Symbol { get; set; }

        public decimal? UsefulUnrealisedPnl
        {
            get
            {
                if(UnrealisedPnl!= null)
                {
                    return Math.Round(((decimal)UnrealisedPnl / 100000000), 4);
                }
                else
                {
                    return 0;
                }
            }
        }
    }

    public class Order
    {
        [JsonProperty(PropertyName = "timeStamp")]
        public DateTime TimeStamp { get; set; }
        [JsonProperty(PropertyName = "symbol")]
        public string Symbol { get; set; }
        [JsonProperty(PropertyName = "ordStatus")]
        public string OrdStatus { get; set; }
        [JsonProperty(PropertyName = "ordType")]
        public string OrdType { get; set; }
        [JsonProperty(PropertyName = "orderID")]
        public string OrderId { get; set; }
        [JsonProperty(PropertyName = "side")]
        public string Side { get; set; }
        [JsonProperty(PropertyName = "price")]
        public decimal? Price { get; set; }
        [JsonProperty(PropertyName = "orderQty")]
        public int? OrderQty { get; set; }
        [JsonProperty(PropertyName = "displayQty")]
        public int? DisplayQty { get; set; }
        [JsonProperty(PropertyName = "execInst")]
        public string ExecInst { get; set; }
        [JsonProperty(PropertyName = "clOrdID")]
        public string ClOrdID { get; set; }

        // below has just been deprecated
        [JsonIgnore]
        public string ClOrdLinkID { get; set; }
        [JsonIgnore]
        public string ContingencyType { get; set; }

        [JsonProperty(PropertyName = "stopPx")]
        public decimal? StopPx { get; set; }
        [JsonProperty(PropertyName = "pegOffsetVale")]
        public decimal? PegOffsetValue { get; set; }
        [JsonProperty(PropertyName = "pegPriceType")]
        public string PegPriceType { get; set; }
    }

    public class OrderAmend
    {
        public OrderAmend(Order order)
        {
            OrderId = order.OrderId;
            Price = order.Price;
            OrderQty = order.OrderQty;
            StopPx = order.StopPx;
            PegOffsetValue = order.PegOffsetValue;
            PegPriceType = order.PegPriceType;
        }
        [JsonProperty(PropertyName = "orderID")]
        public string OrderId { get; set; }
        [JsonProperty(PropertyName = "price")]
        public decimal? Price { get; set; }
        [JsonProperty(PropertyName = "orderQty")]
        public int? OrderQty { get; set; }
        [JsonProperty(PropertyName = "stopPx")]
        public decimal? StopPx { get; set; }
        [JsonProperty(PropertyName = "pegOffsetVale")]
        public decimal? PegOffsetValue { get; set; }
        [JsonProperty(PropertyName = "pegPriceType")]
        public string PegPriceType { get; set; }
    }

    public class Trade
    {
        public decimal Price { get; set; }
    }

    public class SimpleCandle
    {
        public DateTime TimeStamp { get; set; }
        public decimal Open { get; set; }
        public decimal Close { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Volume { get; set; }
        public int Trades { get; set; }
       
    }
}