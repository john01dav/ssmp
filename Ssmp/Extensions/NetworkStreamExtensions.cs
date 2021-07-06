using System.Net.Sockets;
using System.Threading.Tasks;
using System;
using System.IO;

namespace Ssmp.Extensions
{
    public static class NetworkStreamExtensions
    {
        public static async Task<int> ReadBufferLength(this NetworkStream ns, byte[] buffer)
        {
            var index = 0;

            while (index < buffer.Length)
            {
                int bytes;

                try
                {
                    bytes = await ns.ReadAsync(buffer.AsMemory(index, buffer.Length - index));
                }
                catch (Exception e)
                {
                    if (e is IOException or ObjectDisposedException)
                    {
                        break;
                    }

                    throw;
                }

                if (bytes <= 0)
                {
                    break;
                }

                index += bytes;
            }

            return index;
        }
    }
}