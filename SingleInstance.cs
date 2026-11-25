using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PastDow
{
    /// <summary>Per-user argument forwarding; the pipe grants access only to its owner.</summary>
    sealed class SingleInstance : IDisposable
    {
        static readonly SecurityIdentifier User = WindowsIdentity.GetCurrent().User;
        static readonly string Name = "PastDow.Args." + User.Value;
        readonly Mutex mutex;
        NamedPipeServerStream server;
        bool stopping;
        public bool First { get; }

        public SingleInstance() { mutex = new Mutex(true, "Local\\" + Name, out bool first); First = first; }

        public static bool Forward(string[] args, int timeout)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out))
                {
                    pipe.Connect(timeout);
                    using (var writer = new StreamWriter(pipe)) writer.WriteLine(new JavaScriptSerializer().Serialize(args));
                }
                return true;
            }
            catch { return false; }
        }

        public void Listen(Action<string[]> receive)
        {
            _ = Task.Run(async () =>
            {
                while (!stopping)
                {
                    try
                    {
                        var security = new PipeSecurity();
                        security.SetAccessRuleProtection(true, false);
                        security.AddAccessRule(new PipeAccessRule(User, PipeAccessRights.FullControl, AccessControlType.Allow));
                        using (var pipe = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security))
                        {
                            server = pipe;
                            if (stopping) break;
                            await pipe.WaitForConnectionAsync().ConfigureAwait(false);
                            using (var reader = new StreamReader(pipe))
                            {
                                var text = new System.Text.StringBuilder(); int c;
                                while ((c = reader.Read()) >= 0 && c != '\n' && text.Length <= 1048576) text.Append((char)c);
                                if (text.Length <= 1048576) receive(new JavaScriptSerializer { MaxJsonLength = 1048576 }.Deserialize<string[]>(text.ToString()));
                            }
                        }
                    }
                    catch { if (!stopping) await Task.Delay(100).ConfigureAwait(false); }
                }
            });
        }

        public void Dispose()
        {
            stopping = true;
            try { server?.Dispose(); } catch { }
            if (First) mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
