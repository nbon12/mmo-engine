using System.Net;
using System.Net.Sockets;

namespace MmoEngine.Harness.Network;

/// <summary>
/// UDP send and receive for the probe pair and the stub target, with one policy for errors. A
/// <see cref="SocketException"/> is the network's business (a lost send, or an ICMP "port unreachable"
/// from a departed peer that surfaces on the next receive), so it is skipped. Cancellation or a disposed
/// socket means the caller is stopping.
/// </summary>
public static class UdpIo
{
    /// <summary>Receives one datagram, skipping socket errors; null once the socket is stopped or the token cancelled.</summary>
    public static async Task<UdpReceiveResult?> ReceiveAsync(UdpClient socket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        while (true)
        {
            try
            {
                return await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // Skipped: see the class summary.
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return null;
            }
        }
    }

    /// <summary>Sends one datagram, ignoring socket errors; false once the socket is stopped or the token cancelled.</summary>
    public static async Task<bool> SendAsync(UdpClient socket, byte[] datagram, IPEndPoint to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        try
        {
            await socket.SendAsync(datagram, to, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }
}
