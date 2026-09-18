using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TapRunner.Protocol;

namespace TapRunner.App.Services
{
    /// <summary>
    /// Hosts one side of the named pipe used to talk to the TapRunner.StatusListener plugin running
    /// inside a "tap.exe" child process. One instance is created per Run and disposed when it ends.
    /// </summary>
    public class StatusPipeServer : IDisposable
    {
        readonly string pipeName;
        NamedPipeServerStream server;
        StreamWriter writer;
        CancellationTokenSource cts = new();

        /// <summary>Raised (on a background thread) for each status line received from the plugin.</summary>
        public event Action<string> StatusReceived;

        public string PipeName => pipeName;

        public StatusPipeServer()
        {
            pipeName = "TapRunner_" + Guid.NewGuid().ToString("N");
        }

        /// <summary>Starts listening for the plugin to connect. Does not block.</summary>
        public void Start()
        {
            server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _ = AcceptAndReadLoopAsync(cts.Token);
        }

        async Task AcceptAndReadLoopAsync(CancellationToken token)
        {
            try
            {
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };

                using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                        break; // client disconnected

                    try
                    {
                        StatusReceived?.Invoke(line);
                    }
                    catch
                    {
                        // A subscriber throwing on one message must never kill this loop - that
                        // would silently stop every status update for the rest of the run. Drop
                        // this one and keep reading.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected on Dispose
            }
            catch (IOException)
            {
                // pipe broken - the tap.exe process likely exited
            }
            catch (ObjectDisposedException)
            {
                // pipe disposed while waiting/reading
            }
        }

        /// <summary>Sends a graceful stop request to the connected plugin, if any.</summary>
        public void SendStop()
        {
            try
            {
                writer?.WriteLine(StatusProtocol.CommandStop);
            }
            catch
            {
                // no listener connected (plugin not enabled in Results settings, or run already ended) - ignore.
            }
        }

        public void Dispose()
        {
            cts.Cancel();
            try { writer?.Dispose(); } catch { }
            try { server?.Dispose(); } catch { }
        }
    }
}
