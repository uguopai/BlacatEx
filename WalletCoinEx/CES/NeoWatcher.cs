﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading;
using log4net;
using Newtonsoft.Json.Linq;
using ThinNeo;

namespace CES
{
    public class NeoWatcher
    {
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        public static void Start()
        {
            Logger.Info("Neo Watcher Start! Index: " + Config.neoIndex);
            while (true)
            {
                try
                {
                    var count = Config.GetNeoHeight();
                    while (Config.neoIndex < count)
                    {
                        if (Config.neoIndex % 100 == 0)
                        {
                            Logger.Info("Parse NEO Height:" + Config.neoIndex);
                        }
                        var transRspList = ParseNeoBlock(Config.neoIndex, Config.myAccountDic["cneo"]);
                        MyHelper.SendTransInfo(transRspList);
                        DbHelper.SaveIndex(Config.neoIndex, "neo");
                        Config.neoIndex++;
                    }

                    if (count + 1 == Config.neoIndex)
                        Thread.Sleep(1000);

                }
                catch (Exception e)
                {
                    Logger.Error("neo " + e.Message);
                    Thread.Sleep(5000);
                    continue;
                }
            }
        }

        private static List<TransactionInfo> ParseNeoBlock(int i, string address)
        {
            var transRspList = new List<TransactionInfo>();
            var block = _getBlock(i);
            var txs = (JArray)block["tx"];
            foreach (JObject tx in txs)
            {
                var txid = (string)tx["txid"];
                var type = (string)tx["type"];
                if (type == "InvocationTransaction")
                {
                    var notify = _getNotify(txid);
                    if (notify != null && notify.Count > 0)
                    {
                        foreach (JObject n in notify)
                        {
                            //过滤 事件太多，只监视关注的合约
                            var contract = (string) n["contract"];
                            if (contract != "0x" + Config.tokenHashDic["cneo"])
                                continue;

                            var value = n["state"] as JObject;
                            var method = (value["value"] as JArray)[0] as JObject;
                            var name = Encoding.UTF8.GetString(
                                Helper.HexString2Bytes((string) method["value"]));

                            if (name == "transfer")
                            {
                                var to = (value["value"] as JArray)[2] as JObject;
                                if (string.IsNullOrEmpty((string) to["value"]))
                                    continue;
                                var to_address =
                                    Helper_NEO.GetAddress_FromScriptHash(Helper.HexString2Bytes((string) to["value"]));
                                if (to_address == address)
                                {
                                    var neoTrans = new TransactionInfo();
                                    var from = (value["value"] as JArray)[1] as JObject;
                                    var from_address =
                                        Helper_NEO.GetAddress_FromScriptHash(
                                            Helper.HexString2Bytes((string) from["value"]));
                                    var amount = (value["value"] as JArray)[3] as JObject;
                                    var transAmount =
                                        (decimal) new BigInteger(
                                            Helper.HexString2Bytes((string) amount["value"])) /
                                        Config.factorDic["cneo"];
                                    neoTrans.toAddress = address;
                                    neoTrans.coinType = "cneo";
                                    neoTrans.confirmcount = 1;
                                    neoTrans.fromAddress = from_address;
                                    neoTrans.height = i;
                                    neoTrans.txid = txid;
                                    neoTrans.value = transAmount;
                                    transRspList.Add(neoTrans);
                                    Logger.Info(i + " Aave A Cneo Transaction From :" + from_address +
                                                  "; Value:" + transAmount + "; Txid:" + txid);

                                }
                            }
                        }
                    }
                }
            }

            return transRspList;

        }

        static JObject _getBlock(int block)
        {
            WebClient wc = new WebClient();
            var getcounturl = Config.apiDic["neo"] + "?jsonrpc=2.0&id=1&method=getblock&params=[" + block + ",1]";
            var info = wc.DownloadString(getcounturl);
            var json = JObject.Parse(info);
            if (info.Contains("result") == false)
                return null;
            return (JObject)(((JArray)json["result"])[0]);
        }

        static JArray _getNotify(string txid)
        {
            WebClient wc = new WebClient();

            var getcounturl = Config.apiDic["neo"] + "?jsonrpc=2.0&id=1&method=getnotify&params=[\"" + txid + "\"]";
            var info = wc.DownloadString(getcounturl);
            var json = JObject.Parse(info);
            if (json.ContainsKey("result") == false)
                return null;
            var result = (JObject)(((JArray)json["result"])[0]);
            var executions = ((JArray)result["executions"])[0] as JObject;

            return executions["notifications"] as JArray;

        }
    }
}
