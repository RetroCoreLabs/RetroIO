//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//


using System;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace RetroIO.Link.Physical
{

    public class TCPServer
    {
        private readonly Microsoft.Extensions.Logging.ILogger _logger;

        IPEndPoint? localEP = null;
        Socket? listener = null;
        Socket? clientSocket = null;

        // Define the delegate and the event
        public delegate void ReceivedData(byte data);
        public event ReceivedData? OnReceivedData;


        public TCPServer(Microsoft.Extensions.Logging.ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));            
            
        }



        /// <summary>
        /// Start a TelnetServer on the given port
        /// </summary>
        /// <param name="hostIP">Blank means listening on all IPAdresses.</param>
        /// <param name="port">What port to listen on</param>
        /// <returns></returns>
        public bool StartServer(string hostIP, int port)
        {
            if (hostIP.Equals("localhost")) hostIP = "";

            if (string.IsNullOrEmpty(hostIP))
            {
                localEP = new IPEndPoint(IPAddress.Any, port);
            }
            else
            {
                localEP = new IPEndPoint(IPAddress.Parse(hostIP), port);
            }

            if (localEP == null)
            {
                _logger.LogDebug("Didnt find ip4 network");
                return false;
            }


            _logger.LogDebug($"Network server listening on local address and port : {localEP.ToString()}");

            StartListen();


            return true;
        }

        void StartListen()
        {
            // localEP is initialized in the caller before StartListen is invoked; assert the invariant.
            if (localEP == null)
                throw new InvalidOperationException("Local endpoint not initialized before StartListen().");

            try
            {
                listener = new Socket(localEP.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                listener.Blocking = false;

                // Disable Nagle’s Algorithm for real-time responsiveness
                listener.NoDelay = true;

                listener.Bind(localEP);
                listener.Listen(10);
            }
            catch (Exception e)
            {

                _logger.LogDebug($"StartListen, exception {e.ToString()}");
            }
        }


        void StopListening()
        {
            try
            {
                if (listener != null)
                {
                    listener.Close();
                    listener = null;
                }

            }
            catch (Exception ex)
            {
                _logger.LogDebug($"StopListening, exception {ex.ToString()}");
            }
        }

        /// <summary>
        /// Clock ticker. Must be called quite often to make sure that data is sent/received and new connections are allowed
        /// But not to often, as this will take a few microseconds to a milisecond to process.
        /// </summary>

        public void ClockTick()
        {

            TryAccept();

            //if (connectionStates.Count > 0)
            {

                //TrySend();
                if (!TryReceive())
                    RemoveConnection();
            }
        }

        public bool SendByte(byte data)
        {

            if (clientSocket == null) return false;
            if (!clientSocket.Connected) return false;

            try
            {
                byte[] databuf = new byte[1];
                databuf[0] = data;
                int len = clientSocket.Send(databuf);
                if (len == 0) return true;
            }
            catch (SocketException sox)
            {
                _logger.LogDebug($"Unexpected SocketException in SendByte: {sox.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Unexpected in SendByte: {ex.Message}");

            }
            return false;
        }
        int receivedBytes;

        private bool TryReceive()
        {
            byte[] receiveBuf = new byte[1024];

            if (clientSocket == null) return false;
            if (!clientSocket.Connected) return false;

            try
            {
                if (clientSocket.Poll(10, SelectMode.SelectError))
                {
                    return false;
                }

                if (clientSocket.Poll(10, SelectMode.SelectRead))
                {
                    int avail = clientSocket.Available;
                    if (avail == 0) return false; // the client has disconneced
                    if (avail > 0)
                    {
                        if (avail > receiveBuf.Length)
                            avail = receiveBuf.Length;

                        int len = clientSocket.Receive(receiveBuf, 0, avail, SocketFlags.None);

                        this.receivedBytes += len;

                        if (len == 0) return false; // the client has disconneced



                        if (len > 0)
                        {
                            if (OnReceivedData != null)
                            {
                                for (int i = 0; i < len; i++)
                                {
                                    OnReceivedData(receiveBuf[i]);
                                }
                            }
                            Array.Clear(receiveBuf, 0, len);
                            return true;
                        }
                    }
                }

                return true;
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode != SocketError.WouldBlock)
                {
                    _logger.LogDebug("SocketException in tryReceive " + e.ToString());
                    return true; // allow this without disconnecting (?)
                }

            }


            catch (Exception ex)
            {

                _logger.LogDebug("Exception in TryReceive " + ex.ToString()); ;
            }


            return false;
        }

        /// <summary>
        /// Disconnect client
        /// </summary>
        /// <param name="connection"></param>
        private void RemoveConnection()
        {
            if (clientSocket != null)
            {
                try
                {
                    clientSocket.Disconnect(false);
                    clientSocket.Close();

                }
                catch (Exception ex)
                {
                    _logger.LogDebug($"Unexpected in RemoveConnection: {ex.Message}");
                }

                clientSocket.Dispose();
                clientSocket = null;

            }
        }


        private void TryAccept()
        {
            try
            {
                if ((listener != null) && (clientSocket == null))
                {
                    if (listener.Poll(10, SelectMode.SelectRead))
                    {
                        clientSocket = listener.Accept();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Exception in TryAccept " + ex.ToString());
            }
        }

    }
}


