using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
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
                    // pastdow: links carry cookies and the pairing key: only hand them to a server run by this same
                    // user, never to a process of another account that created the pipe name first
                    if (!OwnedByMe(pipe)) return false;
                    using (var writer = new StreamWriter(pipe)) writer.WriteLine(new JavaScriptSerializer().Serialize(args));
                }
                return true;
            }
            catch { return false; }
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        /// <summary>The process at the other end of the pipe runs under the current user's account.</summary>
        static bool OwnedByMe(NamedPipeClientStream pipe)
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)) return false;
                using (var p = Process.GetProcessById((int)pid))
                {
                    if (!OpenProcessToken(p.Handle, 0x0008 /* TOKEN_QUERY */, out token)) return false;
                    using (var id = new WindowsIdentity(token)) return id.User != null && id.User.Equals(User);
                }
            }
            catch { return false; }   // another account's process cannot even be opened: not ours
            finally { if (token != IntPtr.Zero) CloseHandle(token); }
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
