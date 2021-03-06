﻿/*
 * This file is part of the OpenNos Emulator Project. See AUTHORS file for Copyright information
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 */

using OpenNos.Core.Networking.Communication.Scs.Communication.EndPoints;
using OpenNos.Core.Networking.Communication.Scs.Communication.EndPoints.Tcp;
using OpenNos.Core.Networking.Communication.Scs.Communication.Messages;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OpenNos.Core.Networking.Communication.Scs.Communication.Channels.Tcp
{
    /// <summary>
    /// This class is used to communicate with a remote application over TCP/IP protocol.
    /// </summary>
    public class TcpCommunicationChannel : CommunicationChannelBase
    {
        #region Members

        /// <summary>
        /// Size of the buffer that is used to receive bytes from TCP socket.
        /// </summary>
        private const int ReceiveBufferSize = 4 * 1024;

        /// <summary>
        /// This buffer is used to receive bytes
        /// </summary>
        private readonly byte[] _buffer;

        /// <summary>
        /// Socket object to send/reveice messages.
        /// </summary>
        private readonly Socket _clientSocket;

        // 4KB
        private readonly ScsTcpEndPoint _remoteEndPoint;

        /// <summary>
        /// This object is just used for thread synchronizing (locking).
        /// </summary>
        private readonly object _syncLock;

        private bool _disposed;
        private ConcurrentQueue<byte[]> _highPriorityBuffer;
        private ConcurrentQueue<byte[]> _lowPriorityBuffer;
        private Random _random = new Random();

        /// <summary>
        /// A flag to control thread's running
        /// </summary>
        private volatile bool _running;

        private CancellationTokenSource _sendCancellationToken = new CancellationTokenSource();

        private Task _sendTask;

        #endregion

        #region Instantiation

        /// <summary>
        /// Creates a new TcpCommunicationChannel object.
        /// </summary>
        /// <param name="clientSocket">
        /// A connected Socket object that is used to communicate over network
        /// </param>
        public TcpCommunicationChannel(Socket clientSocket)
        {
            _clientSocket = clientSocket;
            _clientSocket.NoDelay = true;

            // initialize lagging mode
            bool isLagMode = System.Configuration.ConfigurationManager.AppSettings["LagMode"].ToLower() == "true";

            var ipEndPoint = (IPEndPoint)_clientSocket.RemoteEndPoint;
            _remoteEndPoint = new ScsTcpEndPoint(ipEndPoint.Address.ToString(), ipEndPoint.Port);

            _buffer = new byte[ReceiveBufferSize];
            _syncLock = new object();

            _highPriorityBuffer = new ConcurrentQueue<byte[]>();
            _lowPriorityBuffer = new ConcurrentQueue<byte[]>();
            CancellationToken cancellationToken = _sendCancellationToken.Token;
            _sendTask = StartSending(SendInterval, new TimeSpan(0, 0, 0, 0, isLagMode ? 1000 : 10), cancellationToken);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the endpoint of remote application.
        /// </summary>
        public override ScsEndPoint RemoteEndPoint
        {
            get
            {
                return _remoteEndPoint;
            }
        }

        #endregion

        #region Methods

        public static async Task StartSending(Action action, TimeSpan period, CancellationToken _sendCancellationToken)
        {
            while (!_sendCancellationToken.IsCancellationRequested)
            {
                await Task.Delay(period, _sendCancellationToken);

                if (!_sendCancellationToken.IsCancellationRequested)
                {
                    action();
                }
            }
        }

        public override async Task ClearLowPriorityQueue()
        {
            _lowPriorityBuffer.Clear();
        }

        /// <summary>
        /// Disconnects from remote application and closes channel.
        /// </summary>
        public override void Disconnect()
        {
            if (CommunicationState != CommunicationStates.Connected)
            {
                return;
            }

            _running = false;
            try
            {
                _sendCancellationToken.Cancel();
                if (_clientSocket.Connected)
                {
                    _clientSocket.Close();
                }

                _clientSocket.Dispose();
            }
            catch
            {
            }
            finally
            {
                _sendCancellationToken.Dispose();
            }

            CommunicationState = CommunicationStates.Disconnected;
            OnDisconnected();
        }

        /// <summary>
        /// Calls Disconnect method.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                Dispose(true);
                GC.SuppressFinalize(this);
                _disposed = true;
            }
        }

        public void SendInterval()
        {
            try
            {
                if (WireProtocol != null)
                {
                    SendByPriority(_highPriorityBuffer);
                    SendByPriority(_lowPriorityBuffer);
                }
            }
            catch (Exception)
            {
                // disconnect
            }

            if (!_clientSocket.Connected)
            {
                return;
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disconnect();
                _sendCancellationToken.Dispose();
            }
        }

        /// <summary>
        /// Sends a message to the remote application.
        /// </summary>
        /// <param name="message">Message to be sent</param>
        protected override void SendMessagepublic(IScsMessage message, byte priority)
        {
            if (priority > 5)
            {
                _highPriorityBuffer.Enqueue(WireProtocol.GetBytes(message));
            }
            else
            {
                _lowPriorityBuffer.Enqueue(WireProtocol.GetBytes(message));
            }
        }

        /// <summary>
        /// Starts the thread to receive messages from socket.
        /// </summary>
        protected override void Startpublic()
        {
            _running = true;
            _clientSocket.BeginReceive(_buffer, 0, _buffer.Length, 0, new AsyncCallback(ReceiveCallback), null);
        }

        private static void SendCallback(IAsyncResult ar)
        {
            try
            {
                // Retrieve the socket from the state object.
                Socket client = (Socket)ar.AsyncState;

                if (!client.Connected)
                {
                    return;
                }

                // Complete sending the data to the remote device.
                int bytesSent = client.EndSend(ar);
            }
            catch (Exception)
            {
                // disconnect
            }
        }

        /// <summary>
        /// This method is used as callback method in _clientSocket's BeginReceive method. It
        /// reveives bytes from socker.
        /// </summary>
        /// <param name="ar">Asyncronous call result</param>
        private void ReceiveCallback(IAsyncResult ar)
        {
            if (!_running)
            {
                return;
            }

            try
            {
                var bytesRead = -1;

                // Get received bytes count
                bytesRead = _clientSocket.EndReceive(ar);

                if (bytesRead > 0)
                {
                    LastReceivedMessageTime = DateTime.Now;

                    // Copy received bytes to a new byte array
                    var receivedBytes = new byte[bytesRead];
                    Array.Copy(_buffer, receivedBytes, bytesRead);

                    // Read messages according to current wire protocol
                    var messages = WireProtocol.CreateMessages(receivedBytes);

                    // Raise MessageReceived event for all received messages
                    foreach (var message in messages)
                    {
                        OnMessageReceived(message, DateTime.Now);
                    }
                }
                else
                {
                    Logger.Log.Warn(Language.Instance.GetMessageFromKey("CLIENT_DISCONNECTED"));
                    Disconnect();
                }

                // Read more bytes if still running
                if (_running)
                {
                    _clientSocket.BeginReceive(_buffer, 0, _buffer.Length, 0, new AsyncCallback(ReceiveCallback), null);
                }
            }
            catch
            {
                Disconnect();
            }
        }

        private void SendByPriority(ConcurrentQueue<byte[]> buffer)
        {
            IEnumerable<byte> outgoingPacket = new List<byte>();

            // send maximal 30 packets at once
            for (int i = 0; i < 30; i++)
            {
                byte[] message;
                if (buffer.TryDequeue(out message) && message != null)
                {
                    outgoingPacket = outgoingPacket.Concat(message);
                }
                else
                {
                    break;
                }
            }

            if (outgoingPacket.Any())
            {
                _clientSocket.BeginSend(outgoingPacket.ToArray(), 0, outgoingPacket.Count(), SocketFlags.None,
                new AsyncCallback(SendCallback), _clientSocket);
            }
        }

        #endregion
    }
}