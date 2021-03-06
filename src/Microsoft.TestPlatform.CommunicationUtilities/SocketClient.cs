// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.TestPlatform.CommunicationUtilities
{
    using System;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.Interfaces;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel;
    using Microsoft.VisualStudio.TestPlatform.Utilities;

    /// <summary>
    /// Communication client implementation over sockets.
    /// </summary>
    public class SocketClient : ICommunicationClient
    {
        private readonly CancellationTokenSource cancellation;
        private readonly TcpClient tcpClient;
        private readonly Func<Stream, ICommunicationChannel> channelFactory;
        private ICommunicationChannel channel;
        private Stream stream;
        private bool stopped;

        public SocketClient()
            : this(stream => new LengthPrefixCommunicationChannel(stream))
        {
        }

        protected SocketClient(Func<Stream, ICommunicationChannel> channelFactory)
        {
            // Used to cancel the message loop
            this.cancellation = new CancellationTokenSource();
            this.stopped = false;

            this.tcpClient = new TcpClient { NoDelay = true };
            this.channelFactory = channelFactory;
        }

        /// <inheritdoc />
        public event EventHandler<ConnectedEventArgs> ServerConnected;

        /// <inheritdoc />
        public event EventHandler<DisconnectedEventArgs> ServerDisconnected;

        /// <inheritdoc />
        public void Start(string connectionInfo)
        {
            this.tcpClient.ConnectAsync(IPAddress.Loopback, int.Parse(connectionInfo))
                .ContinueWith(this.OnServerConnected);
        }

        /// <inheritdoc />
        public void Stop()
        {
            if (!this.stopped)
            {
                EqtTrace.Info("SocketClient: Stop: Cancellation requested. Stopping message loop.");
                this.cancellation.Cancel();
            }
        }

        private void OnServerConnected(Task connectAsyncTask)
        {
            if (connectAsyncTask.IsFaulted)
            {
                throw connectAsyncTask.Exception;
            }

            this.stream = new BufferedStream(this.tcpClient.GetStream(), 8 * 1024);
            this.channel = this.channelFactory(this.stream);
            if (this.ServerConnected != null)
            {
                this.ServerConnected.SafeInvoke(this, new ConnectedEventArgs(this.channel), "SocketClient: ServerConnected");

                // Start the message loop
                Task.Run(() => this.tcpClient.MessageLoopAsync(
                        this.channel,
                        this.Stop,
                        this.cancellation.Token))
                    .ConfigureAwait(false);
            }
        }

        private void Stop(Exception error)
        {
            if (!this.stopped)
            {
                // Do not allow stop to be called multiple times.
                this.stopped = true;

                // Close the client and dispose the underlying stream
                // Depending on implementation order of dispose may be important.
                // 1. Channel dispose -> disposes reader/writer which call a Flush on the Stream. Stream shouldn't
                // be disposed at this time.
                // 2. Stream's dispose may Flush the underlying base stream (if it's a BufferedStream). We should try
                // dispose it next.
                // 3. TcpClient's dispose will clean up the network stream and close any sockets. NetworkStream's dispose
                // is a no-op if called a second time.
                this.channel?.Dispose();
                this.stream?.Dispose();
                this.tcpClient?.Dispose();

                this.cancellation.Dispose();

                this.ServerDisconnected?.SafeInvoke(this, new DisconnectedEventArgs(), "SocketClient: ServerDisconnected");
            }
        }
    }
}
