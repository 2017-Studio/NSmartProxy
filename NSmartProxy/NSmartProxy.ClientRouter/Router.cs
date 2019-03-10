﻿using NSmartProxy.Data;
using NSmartProxy.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NSmartProxy.Client
{
    public class Router
    {
        public int provider_port = 9973;//default value

        public const int PROVIDER_CONFIG_SERVICE_PORT = 12307; //default value
        CancellationTokenSource CANCELTOKEN = new CancellationTokenSource();
        CancellationTokenSource TRANSFERING_TOKEN = new CancellationTokenSource();
        ServerConnnectionManager ConnnectionManager;

        internal static Config ClientConfig;

        //inject
        internal static INSmartLogger Logger;

        public Router(INSmartLogger logger)
        {
            Logger = logger;
        }

        public void SetConifiguration(Config config)
        {
            ClientConfig = config;
        }

        /// <summary>
        /// 重要：连接服务端
        /// </summary>
        /// <returns></returns>
        public async Task ConnectToProvider()
        {
            var appIdIpPortConfig = ClientConfig.Clients;

            ConnnectionManager = ServerConnnectionManager.Create();
            ConnnectionManager.ClientGroupConnected += ServerConnnectionManager_ClientGroupConnected;
            var clientModel = await ConnnectionManager.InitConfig();
            int counter = 0;
            //appid为0时说明没有分配appid，所以需要分配一个
            foreach (var app in appIdIpPortConfig)
            {
                if (app.AppId == 0)
                {
                    app.AppId = clientModel.AppList[counter].AppId;
                    counter++;
                }
            }
            Console.WriteLine("****************port list*************");

            foreach (var ap in clientModel.AppList)
            {
                var cApp = appIdIpPortConfig.First(obj => obj.AppId == ap.AppId);
                Console.WriteLine(ap.AppId.ToString() + ":  " + ClientConfig.ProviderAddress + ":" + ap.Port.ToString() + "=>" +
                     cApp.IP + ":" + cApp.TargetServicePort);
            }
            Console.WriteLine("**************************************");
            Task pollingTask = ConnnectionManager.PollingToProvider();
            try
            {
                await pollingTask;
            }
            catch (Exception ex)
            {
                Logger.Error("Thread:" + Thread.CurrentThread.ManagedThreadId + " crashed.\n", ex);
                throw;
            }
        }

        private void ServerConnnectionManager_ClientGroupConnected(object sender, EventArgs e)
        {
            var args = (ClientGroupEventArgs)e;
            foreach (TcpClient providerClient in args.NewClients)
            {

                Router.Logger.Debug("Open server connection.");
                OpenTrasferation(args.App.AppId, providerClient);
            }

        }

        private async Task OpenTrasferation(int appId, TcpClient providerClient)
        {
            try
            {
                byte[] buffer = new byte[4096];
                NetworkStream providerClientStream = providerClient.GetStream();
                //接收首条消息，首条消息中返回的是appid和客户端
                int readByteCount = await providerClientStream.ReadAsync(buffer, 0, buffer.Length);
                //从空闲连接列表中移除
                ConnnectionManager.RemoveClient(appId, providerClient);
                Router.Logger.Debug(appId + "接受到首条信息");
                TcpClient toTargetServer = new TcpClient();
                //根据clientid_appid发送到固定的端口
                ClientApp item = ClientConfig.Clients.First((obj) => obj.AppId == appId);
                // item1:app编号，item2:ip地址，item3:目标服务端口
                toTargetServer.Connect(item.IP, item.TargetServicePort);
                NetworkStream targetServerStream = toTargetServer.GetStream();
                targetServerStream.Write(buffer, 0, readByteCount);
                await TcpTransferAsync(providerClientStream, targetServerStream);
                //close connection
                providerClient.Close();
                Router.Logger.Debug("关闭一条连接");
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }

        }


        private async Task TcpTransferAsync(NetworkStream providerStream, NetworkStream targetServceStream)
        {
            Router.Logger.Debug("Looping start.");
            //创建相互转发流
            var taskT2PLooping = ToStaticTransfer(TRANSFERING_TOKEN.Token, targetServceStream, providerStream, "T2P");
            var taskP2TLooping = StreamTransfer(TRANSFERING_TOKEN.Token, providerStream, targetServceStream, "P2T");


            //close connnection,whether client or server stopped transferring.
            var comletedTask = await Task.WhenAny(taskT2PLooping, taskP2TLooping);
            Router.Logger.Debug(comletedTask.Result + "传输关闭，重新读取字节");
        }



        private async Task<string> StreamTransfer(CancellationToken ct, NetworkStream fromStream, NetworkStream toStream, string signal, Func<byte[], Task<bool>> beforeTransfer = null)
        {
            await fromStream.CopyToAsync(toStream, 4096, ct);
            return signal;
        }


        private async Task<string> ToStaticTransfer(CancellationToken ct, NetworkStream fromStream, NetworkStream toStream, string signal, Func<byte[], Task<bool>> beforeTransfer = null)
        {

            await fromStream.CopyToAsync(toStream, 4096, ct);
            return signal;
        }

        private void SendZero(int port)
        {
            TcpClient tc = new TcpClient();
            tc.Connect("127.0.0.1", port);
            tc.Client.Send(new byte[] { 0 });
        }
    }

}
