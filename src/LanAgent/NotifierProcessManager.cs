using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LanAgent;

internal static class NotifierProcessManager
{
    private static readonly object SyncRoot = new();
    private static readonly TimeSpan LaunchAttemptInterval = TimeSpan.FromMinutes(5);
    private static DateTimeOffset _lastLaunchAttemptAt = DateTimeOffset.MinValue;
    private static uint _lastSessionId = uint.MaxValue;

    public static void EnsureRunning(ILogger logger)
    {
        var sessionId = NativeMethods.WTSGetActiveConsoleSessionId();
        if (sessionId == uint.MaxValue)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_lastSessionId == sessionId && DateTimeOffset.UtcNow - _lastLaunchAttemptAt < LaunchAttemptInterval)
            {
                return;
            }

            _lastSessionId = sessionId;
            _lastLaunchAttemptAt = DateTimeOffset.UtcNow;
        }

        try
        {
            LaunchNotifier(sessionId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to launch notifier for session {SessionId}.", sessionId);
        }
    }

    private static void LaunchNotifier(uint sessionId)
    {
        if (string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return;
        }

        if (!NativeMethods.WTSQueryUserToken(sessionId, out var userToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to query active user token.");
        }

        IntPtr environmentBlock = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;

        try
        {
            if (!NativeMethods.CreateEnvironmentBlock(out environmentBlock, userToken, false))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create environment block.");
            }

            var startupInfo = new NativeMethods.StartupInfo
            {
                Cb = Marshal.SizeOf<NativeMethods.StartupInfo>(),
                Desktop = @"winsta0\default"
            };

            var processInfo = new NativeMethods.ProcessInformation();
            var commandLine = $"\"{Environment.ProcessPath}\" --notifier";
            var workingDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

            if (!NativeMethods.CreateProcessAsUser(
                    userToken,
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    NativeMethods.CreateUnicodeEnvironment | NativeMethods.CreateNoWindow,
                    environmentBlock,
                    workingDirectory,
                    ref startupInfo,
                    out processInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create notifier process.");
            }

            processHandle = processInfo.Process;
            threadHandle = processInfo.Thread;
        }
        finally
        {
            if (environmentBlock != IntPtr.Zero)
            {
                NativeMethods.DestroyEnvironmentBlock(environmentBlock);
            }

            if (processHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(processHandle);
            }

            if (threadHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(threadHandle);
            }

            NativeMethods.CloseHandle(userToken);
        }
    }

    private static class NativeMethods
    {
        public const uint CreateNoWindow = 0x08000000;
        public const uint CreateUnicodeEnvironment = 0x00000400;

        [DllImport("kernel32.dll", SetLastError = false)]
        public static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcessAsUser(
            IntPtr token,
            string? applicationName,
            string commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct StartupInfo
        {
            public int Cb;
            public string? Reserved;
            public string? Desktop;
            public string? Title;
            public int X;
            public int Y;
            public int XSize;
            public int YSize;
            public int XCountChars;
            public int YCountChars;
            public int FillAttribute;
            public int Flags;
            public short ShowWindow;
            public short Reserved2;
            public IntPtr Reserved2Pointer;
            public IntPtr StdInput;
            public IntPtr StdOutput;
            public IntPtr StdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public int ProcessId;
            public int ThreadId;
        }
    }
}
