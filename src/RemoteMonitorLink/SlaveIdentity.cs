using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RemoteMonitorLink
{
    internal sealed class SlaveIdentity : IDisposable
    {
        private const int TokenLength = 32;
        private const int HeaderLength = 40;
        private const int MaxPfxLength = 32768;
        private const int MaxProtectedLength = 65536;
        private static readonly byte[] Magic = { (byte)'R', (byte)'M', (byte)'I', (byte)'1' };
        private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("RemoteMonitorLink.Identity.v1");

        private readonly X509Certificate2 certificate;
        private readonly string pin;
        private readonly string token;
        private bool disposed;

        private SlaveIdentity(X509Certificate2 certificate, string token)
        {
            if (certificate == null || !certificate.HasPrivateKey)
                throw new CryptographicException("Invalid identity certificate.");
            using (RSA rsa = certificate.GetRSAPublicKey())
                if (rsa == null || rsa.KeySize != 2048)
                    throw new CryptographicException("Invalid identity certificate.");

            byte[] ignored;
            if (!LinkProtocol.TryDecodeToken(token, out ignored))
                throw new CryptographicException("Invalid identity credential.");
            Array.Clear(ignored, 0, ignored.Length);

            this.certificate = certificate;
            this.pin = LinkProtocol.ComputePin(certificate.RawData);
            this.token = token;
        }

        internal X509Certificate2 Certificate { get { return certificate; } }
        internal string Pin { get { return pin; } }
        internal string Token { get { return token; } }

        internal static SlaveIdentity Create()
        {
            byte[] tokenBytes = RandomBytes(TokenLength);
            X509Certificate2 certificate = null;
            try
            {
                certificate = CreateCertificate();
                var identity = new SlaveIdentity(certificate, Convert.ToBase64String(tokenBytes));
                certificate = null;
                return identity;
            }
            finally
            {
                Array.Clear(tokenBytes, 0, tokenBytes.Length);
                if (certificate != null) certificate.Dispose();
            }
        }

        internal static SlaveIdentity LoadOrCreate(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An identity path is required.", "path");
            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath)) return LoadExisting(fullPath);

            string directory = Path.GetDirectoryName(fullPath);
            Directory.CreateDirectory(directory);
            using (SlaveIdentity created = Create())
            {
                string temporaryPath = Path.Combine(directory, Path.GetRandomFileName());
                try
                {
                    WriteProtected(temporaryPath, created);
                    try
                    {
                        File.Move(temporaryPath, fullPath);
                    }
                    catch (IOException)
                    {
                        if (!File.Exists(fullPath)) throw;
                    }
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }
            return LoadExisting(fullPath);
        }

        internal string CreatePairing(IPAddress address, int port)
        {
            ThrowIfDisposed();
            return LinkProtocol.CreatePairing(address, port, pin, token);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            certificate.Dispose();
        }

        private static X509Certificate2 CreateCertificate()
        {
            using (var rsa = new RSACryptoServiceProvider(2048))
            {
                rsa.PersistKeyInCsp = false;
                var request = new CertificateRequest(new X500DistinguishedName("CN=RemoteMonitorLink"), rsa,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                var usages = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
                request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

                using (X509Certificate2 generated = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(20)))
                {
                    byte[] pfx = generated.Export(X509ContentType.Pfx, string.Empty);
                    try
                    {
                        return ImportPfx(pfx);
                    }
                    finally
                    {
                        Array.Clear(pfx, 0, pfx.Length);
                    }
                }
            }
        }

        private static X509Certificate2 ImportPfx(byte[] pfx)
        {
            var certificate = new X509Certificate2(pfx, string.Empty,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new CryptographicException("Invalid identity certificate.");
            }
            return certificate;
        }

        private static void WriteProtected(string path, SlaveIdentity identity)
        {
            byte[] tokenBytes = null;
            byte[] pfx = null;
            byte[] plain = null;
            byte[] protectedBytes = null;
            try
            {
                tokenBytes = Convert.FromBase64String(identity.token);
                pfx = identity.certificate.Export(X509ContentType.Pfx, string.Empty);
                if (pfx.Length < 1 || pfx.Length > MaxPfxLength)
                    throw new CryptographicException("Invalid identity certificate.");

                plain = new byte[HeaderLength + pfx.Length];
                Buffer.BlockCopy(Magic, 0, plain, 0, Magic.Length);
                Buffer.BlockCopy(tokenBytes, 0, plain, Magic.Length, TokenLength);
                byte[] pfxLength = BitConverter.GetBytes(pfx.Length);
                Buffer.BlockCopy(pfxLength, 0, plain, Magic.Length + TokenLength, pfxLength.Length);
                Buffer.BlockCopy(pfx, 0, plain, HeaderLength, pfx.Length);
                protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                if (protectedBytes.Length > MaxProtectedLength)
                    throw new CryptographicException("Invalid identity credential.");

                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(protectedBytes, 0, protectedBytes.Length);
                    stream.Flush(true);
                }
            }
            finally
            {
                Clear(tokenBytes);
                Clear(pfx);
                Clear(plain);
                Clear(protectedBytes);
            }
        }

        private static SlaveIdentity LoadExisting(string path)
        {
            var info = new FileInfo(path);
            if (info.Length < 1 || info.Length > MaxProtectedLength)
                throw new InvalidDataException("Invalid identity file.");

            byte[] protectedBytes = null;
            byte[] plain = null;
            byte[] tokenBytes = null;
            byte[] pfx = null;
            X509Certificate2 certificate = null;
            try
            {
                protectedBytes = File.ReadAllBytes(path);
                if (protectedBytes.Length != info.Length) throw new InvalidDataException("Invalid identity file.");
                plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                if (plain.Length < HeaderLength || !HasMagic(plain)) throw new InvalidDataException("Invalid identity file.");

                int pfxLength = BitConverter.ToInt32(plain, Magic.Length + TokenLength);
                if (pfxLength < 1 || pfxLength > MaxPfxLength || plain.Length != HeaderLength + pfxLength)
                    throw new InvalidDataException("Invalid identity file.");

                tokenBytes = new byte[TokenLength];
                pfx = new byte[pfxLength];
                Buffer.BlockCopy(plain, Magic.Length, tokenBytes, 0, tokenBytes.Length);
                Buffer.BlockCopy(plain, HeaderLength, pfx, 0, pfx.Length);
                certificate = ImportPfx(pfx);
                var identity = new SlaveIdentity(certificate, Convert.ToBase64String(tokenBytes));
                certificate = null;
                return identity;
            }
            catch (CryptographicException)
            {
                throw new InvalidDataException("Invalid identity file.");
            }
            catch (ArgumentException)
            {
                throw new InvalidDataException("Invalid identity file.");
            }
            finally
            {
                if (certificate != null) certificate.Dispose();
                Clear(protectedBytes);
                Clear(plain);
                Clear(tokenBytes);
                Clear(pfx);
            }
        }

        private static bool HasMagic(byte[] bytes)
        {
            int difference = 0;
            for (int i = 0; i < Magic.Length; i++) difference |= bytes[i] ^ Magic[i];
            return difference == 0;
        }

        private static byte[] RandomBytes(int length)
        {
            var bytes = new byte[length];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return bytes;
        }

        private static void Clear(byte[] bytes)
        {
            if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException("SlaveIdentity");
        }
    }
}
