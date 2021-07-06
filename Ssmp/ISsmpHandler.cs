using System.Threading.Tasks;

namespace Ssmp
{
    /// <summary>
    /// The handler that handles incoming Ssmp messages.
    /// </summary>
    public interface ISsmpHandler
    {
        /// <summary>
        /// Handles an incoming Ssmp message.
        /// </summary>
        /// <param name="client">The <see cref="ConnectedClient"/> that is the author of the incoming message.</param>
        /// <param name="message">The incoming message.</param>
        /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
        ValueTask Handle(ConnectedClient client, byte[] message);
    }
}