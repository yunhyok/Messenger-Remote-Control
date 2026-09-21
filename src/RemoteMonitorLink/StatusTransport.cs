using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteMonitorLink
{
    // A fault raised while serving one client (the PowerSI collection callback or this client's response
    // preparation) ends that connection only. Accept-loop and listener faults still stop the server.
    internal sealed class ClientRequestFailedException : IOException
    {
        internal ClientRequestFailedException(string message, Exception inner) : base(message, inner) { }
    }

    internal sealed class StatusServer : IDisposable
    {
        private const int ClientDeadlineMilliseconds = 8000;
        private readonly object gate = new object();
        private readonly SlaveIdentity identity;
        private readonly IPAddress address;
        private readonly int requestedPort;
        private readonly LocalVisionSettings vision;
        private readonly Func<ProcessInventory, CancellationToken, Task<PowerSiReport>> collectPowerSi;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private TcpListener listener;
        private TcpClient activeClient;
        private Task completion = Task.FromResult(0);
        private int boundPort;
        private bool started;
        private bool disposed;

        internal StatusServer(SlaveIdentity identity, IPAddress address, int port, LocalVisionSettings vision = null,
            Func<ProcessInventory, CancellationToken, Task<PowerSiReport>> collectPowerSi = null)
        {
            if (identity == null) throw new ArgumentNullException("identity");
            this.identity = identity;
            this.address = LinkProtocol.NormalizeAddress(address);
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException("port", "Invalid listener port.");
            requestedPort = port;
            this.vision = vision?.Clone();
            this.collectPowerSi = collectPowerSi;
        }

        internal event Action<string> Activity;
        internal event Action<MachineStatus> StatusCaptured;

        internal int Port { get { return Volatile.Read(ref boundPort); } }

        internal Task Completion
        {
            get { lock (gate) return completion; }
        }

        internal void Start()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("StatusServer");
                if (started) throw new InvalidOperationException("The status server is already started.");

                var created = new TcpListener(address, requestedPort);
                created.Start();
                listener = created;
                boundPort = ((IPEndPoint)created.LocalEndpoint).Port;
                started = true;
                completion = RunAsync(created);
            }
            Notify("LISTENING");
        }

        public void Dispose()
        {
            TcpListener listenerToStop;
            TcpClient clientToClose;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                listenerToStop = listener;
                clientToClose = activeClient;
            }
            Close(clientToClose);
            lifetime.Cancel();
            if (listenerToStop != null)
            {
                try { listenerToStop.Stop(); }
                catch (SocketException) { }
            }
        }

        private async Task RunAsync(TcpListener runningListener)
        {
            try
            {
                while (!IsDisposed())
                {
                    TcpClient client;
                    try
                    {
                        client = await runningListener.AcceptTcpClientAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        if (IsDisposed()) break;
                        throw;
                    }
                    catch (SocketException)
                    {
                        if (IsDisposed()) break;
                        throw;
                    }

                    lock (gate)
                    {
                        if (disposed)
                        {
                            Close(client);
                            break;
                        }
                        activeClient = client;
                    }

                    Notify("CLIENT_CONNECTED");
                    try
                    {
                        await HandleClientAsync(client).ConfigureAwait(false);
                        Notify("STATUS_SENT");
                    }
                    catch (Exception exception)
                    {
                        if (!IsDisposed() && !IsClientFailure(exception)) throw;
                        Notify("CLIENT_REJECTED");
                    }
                    finally
                    {
                        lock (gate)
                            if (ReferenceEquals(activeClient, client)) activeClient = null;
                        Close(client);
                    }
                }
            }
            finally
            {
                Notify("STOPPED");
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            client.NoDelay = true;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            using (deadline.Token.Register(Close, client))
            using (var secure = new SslStream(client.GetStream(), false))
            {
                deadline.CancelAfter(ClientDeadlineMilliseconds);
                await LinkProtocol.AwaitWithCancellation(
                    secure.AuthenticateAsServerAsync(identity.Certificate, false, SslProtocols.Tls12, false),
                    deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();

                var reader = new LinkProtocol.LineReader(secure);
                string request = await reader.ReadLineAsync(LinkProtocol.MaxRequestLength, deadline.Token).ConfigureAwait(false);
                byte[] offeredToken;
                bool powerSiOnly;
                if (!LinkProtocol.TryParseRequest(request, out offeredToken, out powerSiOnly))
                    throw new InvalidDataException("Invalid status request.");

                byte[] expectedToken;
                if (!LinkProtocol.TryDecodeToken(identity.Token, out expectedToken))
                {
                    Array.Clear(offeredToken, 0, offeredToken.Length);
                    throw new CryptographicException("Invalid server identity.");
                }
                bool accepted;
                try
                {
                    accepted = LinkProtocol.FixedTimeEquals(offeredToken, expectedToken);
                }
                finally
                {
                    Array.Clear(offeredToken, 0, offeredToken.Length);
                    Array.Clear(expectedToken, 0, expectedToken.Length);
                }
                if (!accepted) throw new InvalidDataException("Status request rejected.");
                // Authentication still has the short deadline. Only an authenticated PWRSI request may wait for local inference.
                if (powerSiOnly) deadline.CancelAfter(PowerSiReport.RequestDeadlineMilliseconds);

                Notify("STATUS_CAPTURING");
                var status = MachineStatus.Capture();
                var processes = await ProcessInventory.CaptureAsync(deadline.Token, powerSiOnly).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                PowerSiObservation powerSi = null;
                PowerSiReport powerSiReport = null;
                if (powerSiOnly)
                {
                    Notify("PWRSI_CAPTURING");
                    if (collectPowerSi == null)
                        powerSi = await PowerSiVision.CaptureAsync(processes, vision, deadline.Token).ConfigureAwait(false);
                    else
                    {
                        try
                        {
                            // The callback reserves its first 100 seconds for collection; the request token leaves response grace.
                            powerSiReport = await collectPowerSi(processes, deadline.Token).ConfigureAwait(false);
                            if (powerSiReport == null) throw new InvalidDataException("PowerSI report is missing.");
                            powerSiReport = powerSiReport.ForTransportCapacity();
                            powerSiReport.Validate();
                        }
                        catch (Exception failure) when (!(failure is OperationCanceledException))
                        {
                            Notify("COLLECT_FAILED");
                            throw new ClientRequestFailedException("PowerSI collection failed.", failure);
                        }
                    }
                    deadline.Token.ThrowIfCancellationRequested();
                }
                status.Processes = processes;
                status.PowerSiOnly = powerSiOnly;
                status.PowerSi = powerSi;
                status.PowerSiReport = powerSiReport;
                string response;
                try
                {
                    if (powerSiReport == null) response = LinkProtocol.FormatStatus(status, powerSiOnly);
                    else
                    {
                        status.Processes = processes.IdentityOnly();
                        try { response = LinkProtocol.FormatStatus(status, true); }
                        finally { status.Processes = processes; }
                    }
                }
                catch (Exception failure) when (!(failure is OperationCanceledException) && !IsClientFailure(failure))
                {
                    Notify("RESPONSE_FAILED");
                    throw new ClientRequestFailedException("Status response could not be prepared.", failure);
                }
                var captured = StatusCaptured;
                if (captured != null)
                {
                    try { captured(status); }
                    catch { } // UI observers cannot change the protocol outcome.
                }
                await LinkProtocol.WriteLineAsync(
                    secure, response, LinkProtocol.MaxResponseLength, deadline.Token).ConfigureAwait(false);
                if (powerSiReport != null)
                    await powerSiReport.WriteFramedAsync(secure, deadline.Token).ConfigureAwait(false);
            }
        }

        private bool IsDisposed()
        {
            lock (gate) return disposed;
        }

        private static bool IsClientFailure(Exception exception)
        {
            return exception is IOException || exception is SocketException ||
                exception is AuthenticationException || exception is InvalidDataException ||
                exception is OperationCanceledException || exception is ObjectDisposedException ||
                exception is CryptographicException;
        }

        private static void Close(object value)
        {
            Close(value as TcpClient);
        }

        private static void Close(TcpClient client)
        {
            if (client == null) return;
            try { client.Close(); }
            catch (SocketException) { }
        }

        private void Notify(string code)
        {
            Action<string> handler = Activity;
            if (handler == null) return;
            try { handler(code); }
            catch { }
        }
    }

    internal static class StatusClient
    {
        private const int ConnectionDeadlineMilliseconds = 8000;
        private const int QueryDeadlineMilliseconds = 120000;

        internal static async Task<MachineStatus> QueryAsync(SlaveEndpoint endpoint, CancellationToken cancellation,
            bool powerSiOnly = false)
        {
            if (endpoint == null) throw new ArgumentNullException("endpoint");
            cancellation.ThrowIfCancellationRequested();

            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var client = new TcpClient(AddressFamily.InterNetwork))
            {
                deadline.CancelAfter(QueryDeadlineMilliseconds);
                using (var connection = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
                using (connection.Token.Register(Close, client))
                {
                    connection.CancelAfter(ConnectionDeadlineMilliseconds);
                    try
                    {
                        await LinkProtocol.AwaitWithCancellation(
                            client.ConnectAsync(endpoint.Address, endpoint.Port), connection.Token).ConfigureAwait(false);
                        connection.Token.ThrowIfCancellationRequested();
                        client.NoDelay = true;

                        byte[] expectedPin;
                        if (!LinkProtocol.TryDecodePin(endpoint.Pin, out expectedPin))
                            throw new InvalidDataException("Invalid status endpoint.");
                        try
                        {
                            int pinAccepted = 0;
                            RemoteCertificateValidationCallback validate = delegate(object sender, X509Certificate certificate,
                                X509Chain chain, SslPolicyErrors errors)
                            {
                                if (certificate == null) return false;
                                byte[] actualPin = null;
                                try
                                {
                                    using (SHA256 sha256 = SHA256.Create())
                                        actualPin = sha256.ComputeHash(certificate.GetRawCertData());
                                    bool equal = LinkProtocol.FixedTimeEquals(expectedPin, actualPin);
                                    if (equal) Interlocked.Exchange(ref pinAccepted, 1);
                                    return equal;
                                }
                                finally
                                {
                                    if (actualPin != null) Array.Clear(actualPin, 0, actualPin.Length);
                                }
                            };

                            using (var secure = new SslStream(client.GetStream(), false, validate))
                            {
                                await LinkProtocol.AwaitWithCancellation(
                                    secure.AuthenticateAsClientAsync("RemoteMonitorLink", null, SslProtocols.Tls12, false),
                                    connection.Token).ConfigureAwait(false);
                                connection.Token.ThrowIfCancellationRequested();
                                if (Interlocked.CompareExchange(ref pinAccepted, 0, 0) != 1)
                                    throw new AuthenticationException("Server certificate pin was not accepted.");
                                connection.CancelAfter(Timeout.Infinite);

                                // The shared token is deliberately unavailable to the wire until pinned TLS succeeds.
                                string request = LinkProtocol.CreateRequest(endpoint.Token, powerSiOnly);
                                await LinkProtocol.WriteLineAsync(
                                    secure, request, LinkProtocol.MaxRequestLength, deadline.Token).ConfigureAwait(false);
                                var reader = new LinkProtocol.LineReader(secure);
                                string response = await reader.ReadLineAsync(
                                    LinkProtocol.MaxResponseLength, deadline.Token).ConfigureAwait(false);
                                deadline.Token.ThrowIfCancellationRequested();
                                MachineStatus result = LinkProtocol.ParseStatus(response, powerSiOnly);
                                if (powerSiOnly && LinkProtocol.IsFramedPowerSiStatus(response))
                                {
                                    result.PowerSiReport = await PowerSiReport.ReadFramedAsync(reader, deadline.Token).ConfigureAwait(false);
                                    LinkProtocol.ValidatePowerSiReportInventory(result.Processes, result.PowerSiReport);
                                    result.Validate();
                                }
                                deadline.Token.ThrowIfCancellationRequested();
                                return result;
                            }
                        }
                        finally
                        {
                            Array.Clear(expectedPin, 0, expectedPin.Length);
                        }
                    }
                    catch (Exception exception)
                    {
                        if (cancellation.IsCancellationRequested && IsCancellationFailure(exception))
                            throw new OperationCanceledException(cancellation);
                        if ((!connection.IsCancellationRequested && !deadline.IsCancellationRequested) ||
                            !IsCancellationFailure(exception)) throw;
                        throw new TimeoutException("Status query timed out.");
                    }
                }
            }
        }

        private static bool IsCancellationFailure(Exception exception)
        {
            return exception is OperationCanceledException || exception is IOException ||
                exception is SocketException || exception is ObjectDisposedException ||
                exception is AuthenticationException;
        }

        private static void Close(object value)
        {
            var client = value as TcpClient;
            if (client == null) return;
            try { client.Close(); }
            catch (SocketException) { }
        }
    }
}
