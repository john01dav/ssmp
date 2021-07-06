using System.Buffers.Binary;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Ssmp.Extensions;

namespace Ssmp
{
    /// <summary>
    /// Represents a connected Ssmp client.
    /// </summary>
    public class ConnectedClient : IDisposable
    {
        private readonly ILogger<ConnectedClient> _logger;
        private readonly TcpClient _tcpClient;
        private readonly NetworkStream _stream;
        private readonly Channel<byte[]> _channel;
        private readonly ISsmpHandler _handler;
        
        /// <summary>
        /// Gets the <see cref="ConnectedClient"/>'s <code>IP:Port</code>.
        /// </summary>
        public string ClientIp { get; }

        /// <summary>
        /// Connects to an Ssmp client.
        /// </summary>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="handler">The incoming message handler.</param>
        /// <param name="ip">The IP of the Ssmp client to connect to.</param>
        /// <param name="port">The port of the Ssmp client to connect to.</param>
        /// <param name="messageQueueLimit">The outgoing messages queue limit.</param>
        /// <returns>New instance of <see cref="ConnectedClient"/>.</returns>
        public static ConnectedClient Connect(ILoggerFactory loggerFactory, ISsmpHandler handler, string ip, int port, int messageQueueLimit) => 
            new(loggerFactory, handler, new TcpClient(ip, port), messageQueueLimit);

        /// <summary>
        /// Adopts an Ssmp client.
        /// </summary>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="handler">The incoming message handler.</param>
        /// <param name="tcpClient">The <see cref="TcpClient"/> to adopt as Ssmp client.</param>
        /// <param name="messageQueueLimit">The outgoing messages queue limit.</param>
        /// <returns>New instance of <see cref="ConnectedClient"/>.</returns>
        public static ConnectedClient Adopt(ILoggerFactory loggerFactory, ISsmpHandler handler, TcpClient tcpClient, int messageQueueLimit) => 
            new(loggerFactory, handler, tcpClient, messageQueueLimit);

        private ConnectedClient(ILoggerFactory loggerFactory, ISsmpHandler handler, TcpClient tcpClient, int messageQueueLimit)
        {
            _logger = loggerFactory.CreateLogger<ConnectedClient>();
            _tcpClient = tcpClient;
            _stream = _tcpClient.GetStream();
            _handler = handler;
            _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(messageQueueLimit)
            {
                AllowSynchronousContinuations = false,
                SingleReader = true,
                SingleWriter = true
            });
            ClientIp = _tcpClient.Client.RemoteEndPoint switch
            {
                IPEndPoint ep => $"{ep.Address}:{ep.Port}",
                _ => "Unknown"
            };
        }

        /// <summary>
        /// Spins the client.
        /// </summary>
        /// <returns>Ended client.</returns>
        public async Task<ConnectedClient> Spin()
        {
            await Task.WhenAll(SendPendingMessages(), ReceiveMessages());
            return this;
        }

        /// <summary>
        /// Enqueues a message to be sent.
        /// </summary>
        /// <param name="message">Message to be sent.</param>
        /// <exception cref="ChannelClosedException">Thrown when channel has been closed.</exception>
        public async ValueTask SendMessage(byte[] message) =>
            await _channel.Writer.WriteAsync(message);
        
        /// <summary>
        /// Attempts to enqueue a message to be sent.
        /// </summary>
        /// <param name="message">Message to be sent.</param>
        /// <returns>true if the item was enqueued; otherwise, false.</returns>
        public bool TrySendMessage(byte[] message) =>
            _channel.Writer.TryWrite(message);

        private async Task SendPendingMessages()
        {
            var lengthBuffer = new byte[4];

            while (_tcpClient.Connected)
            {
                try
                {
                    await foreach (var message in _channel.Reader.ReadAllAsync())
                    {
                        BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, message.Length);
                        await _stream.WriteAsync(lengthBuffer);
                        await _stream.WriteAsync(message);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Could not send pending messages to the client: {clientIp}", ClientIp);
                }
            }
        }
        
        private async Task ReceiveMessages()
        {
            var lengthBuffer = new byte[4];

            while (_tcpClient.Connected)
            {
                //read length
                var readLengthBytes = await _stream.ReadBufferLength(lengthBuffer);

                //return if error, should automatically close the connection;
                if (readLengthBytes != lengthBuffer.Length)
                {
                    _logger.LogWarning(
                        "Disconnected from client {clientIp} while reading the message length. The connection will be treated as closed (most often this error results from the client disconnecting due to a network error/client dying while reading a message).",
                        ClientIp
                    );
                    StopListening();
                    return;
                }
                
                var length = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
                
                //allocate buffer & read message
                var buffer = new byte[length]; //new buffer is allocated so ownership of the buffer can be passed off of this thread
                var readBytes = await _stream.ReadBufferLength(buffer);

                //return if error, should automatically close the connection;
                if (readBytes != buffer.Length)
                {
                    _logger.LogWarning(
                        "Disconnected from client {clientIp} while reading message content bytes. The connection will be treated as closed (most often this error results from the client disconnecting due to a network error/client dying while reading a message).",
                        ClientIp
                    );
                    StopListening();
                    return;
                }

                //handle message
                //rationale for try/catch: error in the handler should not bring down the entire hosted service;
                //it is however better to catch it here so we don't catch everything in the hosted service
                //and end up in an invalid state that can't be recovered from causing undefined behaviour;
                try
                {
                    _logger.LogDebug("Started handling an incoming message from client: {clientIp}.", ClientIp);

                    await _handler.Handle(this, buffer);

                    _logger.LogDebug("Finished handling an incoming message from client: {clientIp}.", ClientIp);
                }
                catch (Exception e)
                {
                    _logger.LogError(
                        e,
                        "An error has occurred in the message handler while handling a message sent from client: {clientIp}.",
                        ClientIp
                    );
                }
            }
        }
        
        /// <summary>
        /// Exits the receive and send loops and closes the TCP connection.
        /// </summary>
        public void StopListening()
        {
            _channel.Writer.Complete();
            _tcpClient.Close();
        }
        
        /// <summary>
        /// Releases all resources used by the <see cref="ConnectedClient"/>.
        /// </summary>
        public void Dispose()
        {
            _stream?.Dispose();
            _tcpClient?.Dispose();
            
            GC.SuppressFinalize(this);
        }
    }
}