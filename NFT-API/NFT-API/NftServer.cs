﻿using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Text;
using ThinNeo;

namespace NFT_API
{
    public class NftServer
    {

        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public static byte[] ExecRequest(HttpListenerContext requestContext)
        {
            //获取客户端传递的参数
            StreamReader sr = new StreamReader(requestContext.Request.InputStream);
            var reqMethod = requestContext.Request.RawUrl.Replace("/", "");
            var data = sr.ReadToEnd();
            byte[] buffer = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new RspInfo() { }));
            var json = new JObject();           
            if (!string.IsNullOrEmpty(data))
                json = JObject.Parse(data);

            try
            {
                Logger.Info($"Have a request:{reqMethod}; post data:{data}");
                buffer = GetResponse(reqMethod, json);
            }

            catch (Exception e)
            {
                var rsp = JsonConvert.SerializeObject(new RspInfo() { state = false, msg = new Error() { error = e.Message } });
                buffer = Encoding.UTF8.GetBytes(rsp);
                Logger.Error(e.Message);
            }

            return buffer;
        }

        private static byte[] GetResponse(string reqMethod, JObject json)
        {
            RspInfo rspInfo = new RspInfo();

            if (reqMethod == "deploy" || reqMethod == "addpoint" || reqMethod == "buy" || reqMethod == "upgrade" || reqMethod == "exchange")
                rspInfo = GetSendrawRsp(reqMethod, json);

            else if (reqMethod == "getmoney")
                rspInfo = SendMoney(json);
            else
                rspInfo = GetInvokeRsp(reqMethod, json);

            return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(rspInfo));

        }

        private static RspInfo SendMoney(JObject json)
        {
            RspInfo rspInfo = new RspInfo() { state = false, msg = new Error() { error = "Input data error!" } };
            if (string.IsNullOrEmpty(json["coinType"].ToString()) || string.IsNullOrEmpty(json["key"].ToString()) || string.IsNullOrEmpty(json["address"].ToString()) || string.IsNullOrEmpty(json["value"].ToString()))
                return rspInfo;
            var nep5Hash = "";
            decimal value = 0;
            string txid = DbHelper.GetSendMoneyTxid(json);
            if (string.IsNullOrEmpty(txid))
            {
                if (json["coinType"].ToString() == "bct")
                {
                    nep5Hash = Config.bctHash;
                    value = Math.Round((decimal)json["value"] * (decimal)10000.0000, 0);
                }
                else if (json["coinType"].ToString() == "bcp")
                {
                    nep5Hash = Config.bcpHash;
                    value = Math.Round((decimal)json["value"] * (decimal)100000000.00000000, 0);
                }
                else
                    return rspInfo;

                var result = NeoController.Nep5TransferAsync(nep5Hash, json["address"].ToString(), value).Result;
                var state = (bool)(JObject.Parse(result)["result"] as JArray)[0]["sendrawtransactionresult"];
                if (state)
                {
                    txid = (JObject.Parse(result)["result"] as JArray)[0]["txid"].ToString();
                    rspInfo = new RspInfo()
                    {
                        state = true,
                        msg = new TransResult()
                        { txid = txid, key = json["key"].ToString() }
                    };
                    DbHelper.SaveSendMoneyResult(json["coinType"].ToString(), json["key"].ToString(), txid, json["address"].ToString(), (decimal)json["value"]);
                }
                else
                {
                    rspInfo = new RspInfo()
                    {
                        state = false,
                        msg = new Error()
                        { error = (JObject.Parse(result)["result"] as JArray)[0]["errorMessage"].ToString() }
                    };
                }

                return rspInfo;
            }
            else
            {
                rspInfo = new RspInfo()
                {
                    state = true,
                    msg = new TransResult()
                    { txid = txid, key = json["key"].ToString() }
                };
            }

            return rspInfo;
        }

        private static RspInfo GetSendrawRsp(string reqMethod, JObject json)
        {
            JArray array = new JArray();
            string result = string.Empty;
            RspInfo rspInfo = new RspInfo();
            
            switch (reqMethod)
            {
                case "deploy":
                case "addpoint":
                    array.Add("(addr)" + json["address"].ToString());
                    break;
                case "buy":
                    array.Add("(hex256)" + json["txid"].ToString());
                    array.Add("(addr)" + json["inviter"].ToString());
                    break;
                case "upgrade":
                    array.Add("(hex256)" + json["txid"].ToString());
                    break;
                case "exchange":
                    array.Add("(addr)" + json["from"].ToString());
                    array.Add("(addr)" + json["to"].ToString());
                    break;
            }
            result = NeoController.SendrawTransactionAsync(array, reqMethod).Result;

            var state = (bool)(JObject.Parse(result)["result"] as JArray)[0]["sendrawtransactionresult"];
            if (state)
            {
                rspInfo = new RspInfo()
                {
                    state = true,
                    msg = new Txid()
                    { txid = (JObject.Parse(result)["result"] as JArray)[0]["txid"].ToString() }
                };
            }
            else
            {
                rspInfo = new RspInfo()
                {
                    state = false,
                    msg = new Error()
                    { error = (JObject.Parse(result)["result"] as JArray)[0]["errorMessage"].ToString() }
                };
            }

            return rspInfo;
        }

        private static RspInfo GetInvokeRsp(string reqMethod, JObject json)
        {
            JArray array = new JArray();
            string result = string.Empty;
            RspInfo rspInfo = new RspInfo();
            JObject stack;
            object resContent = null;

            switch (reqMethod)
            {
                case "getnftinfo":
                    array.Add("(addr)" + json["address"].ToString());
                    result = NeoController.CallInvokescriptAsync(array, reqMethod).Result;
                    stack = (JObject.Parse(result)["result"]["stack"] as JArray)[0] as JObject;
                    resContent = CountNftInfoParse(stack);
                    break;
                case "getnftinfobyid":
                    array.Add("(bytes)" + json["tokenId"].ToString());
                    result = NeoController.CallInvokescriptAsync(array, reqMethod).Result;
                    stack = (JObject.Parse(result)["result"]["stack"] as JArray)[0] as JObject;
                    resContent = CountNftInfoParse(stack);
                    break;

                case "gettxinfo":
                    array.Add("(hex256)" + json["txid"].ToString());
                    result = NeoController.CallInvokescriptAsync(array, reqMethod).Result;
                    stack = (JObject.Parse(result)["result"]["stack"] as JArray)[0] as JObject;
                    resContent = TxInfoParse(stack);
                    break;

                case "getcount":
                    array.Add("(int)" + "1");
                    result = NeoController.CallInvokescriptAsync(array, reqMethod).Result;
                    stack = (JObject.Parse(result)["result"]["stack"] as JArray)[0] as JObject;
                    resContent = CountParse(stack);
                    break;

                case "getconfig":
                    array.Add("(int)" + "1");
                    result = NeoController.CallInvokescriptAsync(array, reqMethod).Result;
                    stack = (JObject.Parse(result)["result"]["stack"] as JArray)[0] as JObject;
                    resContent = ConfigParse(stack);
                    break;

                case "getstate":
                    array.Add("(int)" + "1");
                    result = NeoController.CallInvokescriptAsync(array, reqMethod).Result;
                    stack = (JObject.Parse(result)["result"]["stack"] as JArray)[0] as JObject;
                    if (string.IsNullOrEmpty(stack["value"].ToString()))
                        resContent = Enum.GetName(typeof(ContractState), ContractState.Active);
                    break;

                case "getnotify":
                    var txid = json["txid"].ToString();
                    var height = NeoController.GetHeight();
                    result = NeoController.GetNotifyByTxidAsync(txid).Result;
                    stack = (JObject.Parse(result)["result"]["executions"] as JArray)[0] as JObject;
                    resContent = NotifyInfoParse(stack, height);
                    break;
            }
            rspInfo.state = true;
            rspInfo.msg = resContent;
            return rspInfo;

        }

        private static ExchangeInfo TxInfoParse(JObject stack)
        {
            var txInfo = new ExchangeInfo();
            var value = stack["value"] as JArray;
            if (value == null || value.Count < 3)
                return txInfo;
            if (!string.IsNullOrEmpty(value[0]["value"].ToString()))
                txInfo.@from =
                    Helper_NEO.GetAddress_FromScriptHash(ThinNeo.Helper.HexString2Bytes(value[0]["value"].ToString()));
            if (!string.IsNullOrEmpty(value[1]["value"].ToString()))
                txInfo.to = Helper_NEO.GetAddress_FromScriptHash(ThinNeo.Helper.HexString2Bytes(value[1]["value"].ToString()));
            if (!string.IsNullOrEmpty(value[2]["value"].ToString()))
                txInfo.tokenId = value[2]["value"].ToString();
            return txInfo;
        }

        private static NFTInfo CountNftInfoParse(JObject stack)
        {
            var nftInfo = new NFTInfo();
            var value = stack["value"] as JArray;
            if (value == null || value.Count < 5)
                return nftInfo;
            if (!string.IsNullOrEmpty(value[0]["value"].ToString()))
                nftInfo.TokenId = value[0]["value"].ToString();
            if (!string.IsNullOrEmpty(value[1]["value"].ToString()))
                nftInfo.Owner = Helper_NEO.GetAddress_FromScriptHash(ThinNeo.Helper.HexString2Bytes(value[1]["value"].ToString()));
            if (value[2]["type"].ToString() == "Integer")
                nftInfo.Rank = Convert.ToInt32(value[2]["value"]);
            if (value[3]["type"].ToString() == "Integer")
                nftInfo.ContributionPoint = Convert.ToInt32(value[3]["value"]);
            if (!string.IsNullOrEmpty(value[4]["value"].ToString()))
                nftInfo.InviterTokenId = value[4]["value"].ToString();
            return nftInfo;
        }

        private static NftCount CountParse(JObject stack)
        {
            var count = new NftCount();
            var value = stack["value"] as JArray;
            if (value == null || value.Count < 5)
                return count;
            if (value[0]["type"].ToString() == "Integer")
                count.AllCount = Convert.ToInt32(value[0]["value"]);
            if (value[1]["type"].ToString() == "Integer")
                count.SilverCount = Convert.ToInt32(value[1]["value"]);
            if (value[2]["type"].ToString() == "Integer")
                count.GoldCount = Convert.ToInt32(value[2]["value"]);
            if (value[3]["type"].ToString() == "Integer")
                count.PlatinumCount = Convert.ToInt32(value[3]["value"]);
            if (value[4]["type"].ToString() == "Integer")
                count.DiamondCount = Convert.ToInt32(value[4]["value"]);
            return count;
        }

        private static NotifyInfo NotifyInfoParse(JObject stack, BigInteger height)
        {
            var notifyInfo = new NotifyInfo();

            notifyInfo.BlockHeight = height;

            var notifications = stack["notifications"] as JArray;
            if (notifications.Count == 1) // addpoint  exchange
            {
                var jValue = notifications[0]["state"]["value"] as JArray;
                var method = Encoding.UTF8.GetString(ThinNeo.Helper.HexString2Bytes(jValue[0]["value"].ToString()));
                if (method == "addpoint")
                {
                    notifyInfo.AddPointTokenId = jValue[1]["value"].ToString();
                    notifyInfo.AddPointAddress = Helper_NEO.GetAddress_FromScriptHash(ThinNeo.Helper.HexString2Bytes(jValue[2]["value"].ToString()));
                    notifyInfo.AddPointValue = new BigInteger(ThinNeo.Helper.HexString2Bytes(jValue[3]["value"].ToString()));
                }

                if (method == "exchange")
                {
                    if (!string.IsNullOrEmpty(jValue[1]["value"].ToString()))
                        notifyInfo.ExchangeFrom = Helper_NEO.GetAddress_FromScriptHash(ThinNeo.Helper.HexString2Bytes(jValue[1]["value"].ToString()));
                    if (!string.IsNullOrEmpty(jValue[2]["value"].ToString()))
                        notifyInfo.ExchangeTo = Helper_NEO.GetAddress_FromScriptHash(ThinNeo.Helper.HexString2Bytes(jValue[2]["value"].ToString()));
                    if (!string.IsNullOrEmpty(jValue[3]["value"].ToString()))
                        notifyInfo.ExchangeTokenId = jValue[3]["value"].ToString();
                }
                notifyInfo.NotifyType = method;
            }

            if (notifications.Count == 2) // buy upgrade
            {
                var jValue1 = notifications[0]["state"]["value"] as JArray;
                var method1 = Encoding.UTF8.GetString(ThinNeo.Helper.HexString2Bytes(jValue1[0]["value"].ToString()));
                if (method1 == "addpoint")
                {
                    notifyInfo.AddPointTokenId = jValue1[1]["value"].ToString();
                    notifyInfo.AddPointAddress = Helper_NEO.GetAddress_FromScriptHash(ThinNeo.Helper.HexString2Bytes(jValue1[2]["value"].ToString()));
                    notifyInfo.AddPointValue = new BigInteger(ThinNeo.Helper.HexString2Bytes(jValue1[3]["value"].ToString()));
                }

                var jValue2 = notifications[1]["state"]["value"] as JArray;
                var method2 = Encoding.UTF8.GetString(ThinNeo.Helper.HexString2Bytes(jValue2[0]["value"].ToString()));
                if (method2 == "exchange")
                {
                    if (!string.IsNullOrEmpty(jValue2[1]["value"].ToString()))
                        notifyInfo.ExchangeFrom = Helper_NEO.GetAddress_FromScriptHash(ThinNeo.Helper.HexString2Bytes(jValue2[1]["value"].ToString()));
                    if (!string.IsNullOrEmpty(jValue2[2]["value"].ToString()))
                        notifyInfo.ExchangeTo = Helper_NEO.GetAddress_FromScriptHash(ThinNeo.Helper.HexString2Bytes(jValue2[2]["value"].ToString()));
                    if (!string.IsNullOrEmpty(jValue2[3]["value"].ToString()))
                        notifyInfo.ExchangeTokenId = jValue2[3]["value"].ToString();
                    notifyInfo.NotifyType = "buy";
                }

                if (method2 == "upgrade")
                {
                    notifyInfo.UpgradeTokenId = jValue2[1]["value"].ToString();
                    notifyInfo.UpgradeAddress = Helper_NEO.GetAddress_FromScriptHash(ThinNeo.Helper.HexString2Bytes(jValue2[2]["value"].ToString()));
                    notifyInfo.UpgradeLastRank = BigInteger.Parse(jValue2[3]["value"].ToString());
                    notifyInfo.UpgradenowRank = BigInteger.Parse(jValue2[4]["value"].ToString());
                    notifyInfo.NotifyType = "upgrade";
                }
            }

            return notifyInfo;
        }

        private static object ConfigParse(JObject stack)
        {
            var config = new ConfigInfo();
            var value = stack["value"] as JArray;
            if (value == null || value.Count < 12)
                return config;
            if (value[0]["type"].ToString() == "ByteArray")
                config.SilverPrice = new BigInteger(Helper.HexString2Bytes((string)value[0]["value"])) / 10000;
            if (value[1]["type"].ToString() == "ByteArray")
                config.GoldPrice = new BigInteger(Helper.HexString2Bytes((string)value[1]["value"])) / 10000;
            if (value[2]["type"].ToString() == "ByteArray")
                config.PlatinumPrice = new BigInteger(Helper.HexString2Bytes((string)value[2]["value"])) / 10000;
            if (value[3]["type"].ToString() == "ByteArray")
                config.DiamondPrice = new BigInteger(Helper.HexString2Bytes((string)value[3]["value"])) / 10000;
            if (value[4]["type"].ToString() == "ByteArray")
                config.LeaguerInvitePoint = new BigInteger(Helper.HexString2Bytes((string)value[4]["value"]));
            if (value[5]["type"].ToString() == "ByteArray")
                config.SilverInvitePoint = new BigInteger(Helper.HexString2Bytes((string)value[5]["value"]));
            if (value[6]["type"].ToString() == "ByteArray")
                config.GoldInvitePoint = new BigInteger(Helper.HexString2Bytes((string)value[6]["value"]));
            if (value[7]["type"].ToString() == "ByteArray")
                config.PlatinumInvitePoint = new BigInteger(Helper.HexString2Bytes((string)value[7]["value"]));
            if (value[8]["type"].ToString() == "ByteArray")
                config.DiamondInvitePoint = new BigInteger(Helper.HexString2Bytes((string)value[8]["value"]));
            if (value[9]["type"].ToString() == "ByteArray")
                config.GoldUpgradePoint = new BigInteger(Helper.HexString2Bytes((string)value[9]["value"]));
            if (value[10]["type"].ToString() == "ByteArray")
                config.PlatinumUpgradePoint = new BigInteger(Helper.HexString2Bytes((string)value[10]["value"]));
            if (value[11]["type"].ToString() == "ByteArray")
                config.DiamondUpgradePoint = new BigInteger(Helper.HexString2Bytes((string)value[11]["value"]));
            if (value[11]["type"].ToString() == "ByteArray")
                config.GatheringAddress = Helper_NEO.GetAddress_FromScriptHash(ThinNeo.Helper.HexString2Bytes(value[12]["value"].ToString()));
            return config;
        }
    }
}
