using System.Net.Sockets;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ssmp
{
    /// <summary>
    /// The central Ssmp server service.
    /// </summary>
    public interface ICentralServerService
    {
        /// <summary>
        /// Gets the <see cref="ConnectedClient"/>s.
        /// </summary>
        ImmutableList<ConnectedClient> ConnectedClients { get; }

        /// <summary>
        /// Spins the server once.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        Task SpinOnce();
    }
    
    /// <inheritdoc />
    public class CentralServerService : ICentralServerService
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<CentralServerService> _logger;
        private readonly int _messageQueueLimit;
        private readonly List<Task> _tasks = new();
        private readonly TcpListener _listener;
        private readonly ISsmpHandler _handler;

        private volatile ImmutableList<ConnectedClient> _connectedClients = ImmutableList<ConnectedClient>.Empty;

        /// <inheritdoc />
        public ImmutableList<ConnectedClient> ConnectedClients => _connectedClients;

        /// <summary>
        /// Instantiates a new instance of <see cref="CentralServerService"/>.
        /// </summary>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="options">The Ssmp config.</param>
        /// <param name="handler">The incoming message handler.</param>
        public CentralServerService(
            ILoggerFactory loggerFactory,
            IOptions<SsmpOptions> options,
            ISsmpHandler handler
        )
        {
            var ssmpOptions = options.Value;

            _loggerFactory = loggerFactory;
            _logger = _loggerFactory.CreateLogger<CentralServerService>();
            _handler = handler;
            _messageQueueLimit = ssmpOptions.Port;
            _listener = new TcpListener(IPAddress.Parse(ssmpOptions.IpAddress), ssmpOptions.Port);

            _listener.Start();
        }

        /// <inheritdoc />
        public async Task SpinOnce()
        {
            _tasks.Add(_listener.AcceptTcpClientAsync());

            var completedTask = await Task.WhenAny(_tasks);

            _tasks.Remove(completedTask);

            if (completedTask is Task<TcpClient> newConnectionTask)
            {
                var client = ConnectedClient.Adopt(_loggerFactory, _handler, await newConnectionTask, _messageQueueLimit);
                
                _connectedClients = _connectedClients.Add(client);
                _tasks.Add(client.Spin());
            }
            else if (completedTask is Task<ConnectedClient> endedClientTask)
            {
                var endedClient = await endedClientTask;
                _connectedClients = _connectedClients.Remove(endedClient);

                _logger.LogInformation("Disconnected from client: {clientIp}", endedClient.ClientIp);
                
                endedClient.Dispose();
            }
            else
            {
                await completedTask;
            }
        }
    }
}