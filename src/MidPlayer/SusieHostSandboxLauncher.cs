using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace MidFD;

internal sealed class SusieHostSandbox : IDisposable
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const nuint ProcThreadAttributeSecurityCapabilities = 0x00020009;
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint ProcessDuplicateHandle = 0x00000040;
    private const int ErrorAlreadyExists = 183;
    private const int HResultAlreadyExists = unchecked((int)0x800700B7);

    private readonly IntPtr _jobHandle;
    private readonly IntPtr _processHandle;
    private readonly int _processId;
    private readonly AppContainerProfile _profile;
    private bool _disposed;

    internal static uint HostProcessCreationFlags => CreateSuspended | ExtendedStartupInfoPresent | CreateNoWindow;
    internal static uint ProfileInitializerCreationFlags => ExtendedStartupInfoPresent | CreateNoWindow;

    private SusieHostSandbox(IntPtr jobHandle, IntPtr processHandle, uint processId, string rootDirectory, string hostExecutable, string pluginPath, AppContainerProfile profile)
    {
        _jobHandle = jobHandle;
        _processHandle = processHandle;
        _processId = checked((int)processId);
        _profile = profile;
        RootDirectory = rootDirectory;
        HostExecutable = hostExecutable;
        PluginPath = pluginPath;
    }

    internal string RootDirectory { get; }
    internal string HostExecutable { get; }
    internal string PluginPath { get; }
    internal int ProcessId => _processId;
    internal string DescribeProcessState()
    {
        uint wait = WaitForSingleObject(_processHandle, 0);
        if (wait == 0 && GetExitCodeProcess(_processHandle, out uint exitCode)) return $"exited with code {exitCode}";
        if (wait == 0x102) return "still running";
        return $"state unavailable (wait result 0x{wait:X8}, Win32 {Marshal.GetLastWin32Error()})";
    }
    internal bool HasExited
    {
        get
        {
            uint wait = WaitForSingleObject(_processHandle, 0);
            return wait == 0;
        }
    }

    internal static SusieHostSandbox Start(
        string hostDirectory,
        string hostExecutableName,
        string pluginPath,
        AppContainerProfile profile,
        string pipeName,
        int parentProcessId)
    {
        string fullHostDirectory = Path.GetFullPath(hostDirectory);
        string sourceHostExecutable = Path.Combine(fullHostDirectory, hostExecutableName);
        if (!File.Exists(sourceHostExecutable))
            throw new FileNotFoundException("The architecture-matched Susie Host executable is missing.", sourceHostExecutable);

        string fullPluginPath = Path.GetFullPath(pluginPath);
        if (!File.Exists(fullPluginPath)) throw new FileNotFoundException("The registered Susie Plugin no longer exists.", fullPluginPath);

        IntPtr appContainerSid = profile.Sid;
        string root = string.Empty;
        try
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "MidFD.Susie");
            Directory.CreateDirectory(tempRoot);
            root = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            GrantReadAccess(root, new SecurityIdentifier(appContainerSid));

            string hostStage = Path.Combine(root, "host");
            string pluginStage = Path.Combine(root, "plugin");
            string inputStage = Path.Combine(root, "input");
            string diagnosticsStage = Path.Combine(root, "diagnostics");
            Directory.CreateDirectory(hostStage);
            Directory.CreateDirectory(pluginStage);
            Directory.CreateDirectory(inputStage);
            Directory.CreateDirectory(diagnosticsStage);
            GrantReadWriteAccess(diagnosticsStage, new SecurityIdentifier(appContainerSid));

            CopyFlatDirectory(fullHostDirectory, hostStage);
            CopyDirectoryWithoutReparsePoints(Path.GetDirectoryName(fullPluginPath)!, pluginStage);
            string stagedPlugin = Path.Combine(pluginStage, Path.GetFileName(fullPluginPath));
            if (!File.Exists(stagedPlugin)) throw new IOException("The registered Plugin could not be staged inside its sandbox.");

            string stagedHostExecutable = Path.Combine(hostStage, hostExecutableName);
            IntPtr jobHandle = CreateJobObjectW(IntPtr.Zero, null);
            if (jobHandle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");

            IntPtr processHandle = IntPtr.Zero;
            IntPtr threadHandle = IntPtr.Zero;
            IntPtr attributeList = IntPtr.Zero;
            IntPtr securityCapabilitiesPointer = IntPtr.Zero;
            IntPtr startupInfoPointer = IntPtr.Zero;
            try
            {
                    var limits = new JobObjectExtendedLimitInformation
                    {
                        BasicLimitInformation = new JobObjectBasicLimitInformation
                        {
                            LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitActiveProcess,
                            ActiveProcessLimit = 1
                        }
                    };
                    if (!SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed.");

                    IntPtr attributeBytes = IntPtr.Zero;
                    InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeBytes);
                    attributeList = Marshal.AllocHGlobal(attributeBytes);
                    if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeBytes))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");

                    var securityCapabilities = new SecurityCapabilities
                    {
                        AppContainerSid = appContainerSid,
                        Capabilities = IntPtr.Zero,
                        CapabilityCount = 0,
                        Reserved = 0
                    };
                    securityCapabilitiesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
                    Marshal.StructureToPtr(securityCapabilities, securityCapabilitiesPointer, false);
                    if (!UpdateProcThreadAttribute(
                            attributeList,
                            0,
                            ProcThreadAttributeSecurityCapabilities,
                            securityCapabilitiesPointer,
                            (nuint)Marshal.SizeOf<SecurityCapabilities>(),
                            IntPtr.Zero,
                            IntPtr.Zero))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Setting AppContainer process attributes failed.");
                    }

                    string[] childArguments = { "--serve", pipeName, stagedPlugin, root, parentProcessId.ToString(), "1" };
                    string commandLine = QuoteArgument(stagedHostExecutable) + " " + string.Join(" ", childArguments.Select(QuoteArgument));
                    int startupInfoSize = Marshal.SizeOf<StartupInfoEx>();
                    startupInfoPointer = Marshal.AllocHGlobal(startupInfoSize);
                    Marshal.Copy(new byte[startupInfoSize], 0, startupInfoPointer, startupInfoSize);
                    Marshal.WriteInt32(startupInfoPointer, startupInfoSize);
                    int attributeListOffset = checked((int)Marshal.OffsetOf<StartupInfoEx>(nameof(StartupInfoEx.AttributeList)));
                    Marshal.WriteIntPtr(startupInfoPointer, attributeListOffset, attributeList);
                    if (!CreateProcessW(
                            stagedHostExecutable,
                            new StringBuilder(commandLine),
                            IntPtr.Zero,
                            IntPtr.Zero,
                            false,
                            HostProcessCreationFlags,
                            IntPtr.Zero,
                            null,
                            startupInfoPointer,
                            out ProcessInformation processInformation))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess(AppContainer) failed.");
                    }

                    processHandle = processInformation.Process;
                    threadHandle = processInformation.Thread;
                    if (!AssignProcessToJobObject(jobHandle, processHandle))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed.");
                    if (ResumeThread(threadHandle) == uint.MaxValue)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed.");

                var result = new SusieHostSandbox(jobHandle, processHandle, processInformation.ProcessId, root, stagedHostExecutable, stagedPlugin, profile);
                jobHandle = IntPtr.Zero;
                processHandle = IntPtr.Zero;
                return result;
            }
            catch
            {
                if (processHandle != IntPtr.Zero) TerminateProcess(processHandle, 1);
                throw;
            }
            finally
            {
                if (threadHandle != IntPtr.Zero) CloseHandle(threadHandle);
                if (processHandle != IntPtr.Zero) CloseHandle(processHandle);
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }
                if (startupInfoPointer != IntPtr.Zero) Marshal.FreeHGlobal(startupInfoPointer);
                if (securityCapabilitiesPointer != IntPtr.Zero) Marshal.FreeHGlobal(securityCapabilitiesPointer);
                if (jobHandle != IntPtr.Zero) CloseHandle(jobHandle);
            }
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
            throw;
        }
    }

    internal static AppContainerProfile CreateProfile(string pluginIdentity)
    {
        string profileBaseName = BuildProfileName(pluginIdentity)[..20];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            string profileName = profileBaseName + Guid.NewGuid().ToString("N")[..16];
            int result = CreateAppContainerProfile(profileName, profileName, "MidFD isolated Susie Import Filter host", IntPtr.Zero, 0, out IntPtr sid);
            if (result == 0) return new AppContainerProfile(profileName, sid);
            if (sid != IntPtr.Zero) FreeSid(sid);
            if (result != HResultAlreadyExists && result != ErrorAlreadyExists)
                throw new Win32Exception(result, "The Windows AppContainer identity could not be created.");
        }
        throw new IOException("A unique Windows AppContainer identity could not be allocated.");
    }

    private static string BuildProfileName(string pluginIdentity)
        => "MidFD.Susie." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(pluginIdentity)))[..32];

    private static string GetAppContainerLocalFolder(IntPtr appContainerSid)
    {
        string sid = new SecurityIdentifier(appContainerSid).Value;
        int result = GetAppContainerFolderPath(sid, out IntPtr pathPointer);
        if (result == unchecked((int)0x80070002))
        {
            InitializeAppContainerProfileFolder(appContainerSid);
            result = GetAppContainerFolderPath(sid, out pathPointer);
        }
        if (result != 0) throw new Win32Exception(result, "GetAppContainerFolderPath failed.");
        try
        {
            string? path = Marshal.PtrToStringUni(pathPointer);
            if (string.IsNullOrWhiteSpace(path)) throw new IOException("GetAppContainerFolderPath returned an empty path.");
            return path;
        }
        finally { Marshal.FreeCoTaskMem(pathPointer); }
    }

    private static void InitializeAppContainerProfileFolder(IntPtr appContainerSid)
    {
        string sidBeforeCreate = new SecurityIdentifier(appContainerSid).Value;
        string systemExecutable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe");
        if (!File.Exists(systemExecutable)) throw new FileNotFoundException("Windows whoami.exe is required to initialize the AppContainer profile folder.", systemExecutable);

        IntPtr attributeList = IntPtr.Zero;
        IntPtr securityCapabilitiesPointer = IntPtr.Zero;
        IntPtr startupInfoPointer = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero;
        try
        {
            IntPtr attributeBytes = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeBytes);
            attributeList = Marshal.AllocHGlobal(attributeBytes);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeBytes))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList for AppContainer profile initialization failed.");

            var securityCapabilities = new SecurityCapabilities { AppContainerSid = appContainerSid };
            securityCapabilitiesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
            Marshal.StructureToPtr(securityCapabilities, securityCapabilitiesPointer, false);
            if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeSecurityCapabilities, securityCapabilitiesPointer,
                    (nuint)Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Setting AppContainer profile initialization attributes failed.");

            int startupInfoSize = Marshal.SizeOf<StartupInfoEx>();
            startupInfoPointer = Marshal.AllocHGlobal(startupInfoSize);
            Marshal.Copy(new byte[startupInfoSize], 0, startupInfoPointer, startupInfoSize);
            Marshal.WriteInt32(startupInfoPointer, startupInfoSize);
            int attributeListOffset = checked((int)Marshal.OffsetOf<StartupInfoEx>(nameof(StartupInfoEx.AttributeList)));
            Marshal.WriteIntPtr(startupInfoPointer, attributeListOffset, attributeList);
            string commandLine = QuoteArgument(systemExecutable);
            if (!CreateProcessW(systemExecutable, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    ProfileInitializerCreationFlags, IntPtr.Zero, null, startupInfoPointer, out ProcessInformation processInformation))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Creating the AppContainer profile initializer failed (sidBefore={sidBeforeCreate}, sidAfter={new SecurityIdentifier(appContainerSid).Value}, startup={startupInfoSize}, cb={Marshal.ReadInt32(startupInfoPointer)}, attributeOffset={attributeListOffset}, attributeBytes={attributeBytes}, capabilitiesSize={Marshal.SizeOf<SecurityCapabilities>()}, executable={systemExecutable}).");

            processHandle = processInformation.Process;
            uint waitResult = WaitForSingleObject(processHandle, 10000);
            if (waitResult != 0)
                throw new TimeoutException(waitResult == 0x102 ? "AppContainer profile initialization timed out." : "Waiting for AppContainer profile initialization failed.");
        }
        catch
        {
            if (processHandle != IntPtr.Zero) TerminateProcess(processHandle, 1);
            throw;
        }
        finally
        {
            if (processHandle != IntPtr.Zero) CloseHandle(processHandle);
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (startupInfoPointer != IntPtr.Zero) Marshal.FreeHGlobal(startupInfoPointer);
            if (securityCapabilitiesPointer != IntPtr.Zero) Marshal.FreeHGlobal(securityCapabilitiesPointer);
        }
    }

    internal long DuplicateHandleToHost(IntPtr sourceHandle, uint desiredAccess)
    {
        if (_disposed || HasExited) throw new IOException("The isolated Susie Host is no longer running.");
        IntPtr targetProcess = OpenProcess(ProcessDuplicateHandle, false, checked((uint)ProcessId));
        if (targetProcess == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess for handle transfer failed.");
        try
        {
            if (!DuplicateHandle(GetCurrentProcess(), sourceHandle, targetProcess, out IntPtr remoteHandle, desiredAccess, false, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateHandle to isolated Susie Host failed.");
            return remoteHandle.ToInt64();
        }
        finally { CloseHandle(targetProcess); }
    }

    internal string? ReadHostFailureLog()
    {
        string path = Path.Combine(RootDirectory, "diagnostics", "host-failure.log");
        try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null; }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseHandle(_jobHandle); // KILL_ON_JOB_CLOSE is the failure/timeout boundary.
        WaitForSingleObject(_processHandle, 3000);
        CloseHandle(_processHandle);
        try { Directory.Delete(RootDirectory, recursive: true); } catch { }
        _profile.Dispose();
    }

    internal sealed class AppContainerProfile : IDisposable
    {
        private int _disposed;

        internal AppContainerProfile(string profileName, IntPtr sid)
        {
            ProfileName = profileName;
            Sid = sid;
            SidText = new SecurityIdentifier(sid).Value;
        }

        internal string ProfileName { get; }
        internal IntPtr Sid { get; }
        internal string SidText { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            FreeSid(Sid);
            int result = DeleteAppContainerProfile(ProfileName);
            if (result != 0)
                Services.LogService.Warn($"[Susie Host] DeleteAppContainerProfile failed for {ProfileName}: 0x{result:X8}.");
        }
    }

    private static void GrantReadAccess(string root, SecurityIdentifier sid)
    {
        var security = new DirectorySecurity();
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(root), security);
    }

    private static void GrantReadWriteAccess(string root, SecurityIdentifier sid)
    {
        var security = new DirectorySecurity();
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.Modify | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(root), security);
    }

    private static void CopyFlatDirectory(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }
    }

    private static void CopyDirectoryWithoutReparsePoints(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            string child = Path.Combine(destination, Path.GetFileName(directory));
            Directory.CreateDirectory(child);
            CopyDirectoryWithoutReparsePoints(directory, child);
        }
    }

    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder(value.Length + 2).Append('"');
        int slashCount = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashCount++; continue; }
            if (character == '"')
            {
                result.Append('\\', slashCount * 2 + 1).Append('"');
                slashCount = 0;
                continue;
            }
            result.Append('\\', slashCount).Append(character);
            slashCount = 0;
        }
        result.Append('\\', slashCount * 2).Append('"');
        return result.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityCapabilities
    {
        public IntPtr AppContainerSid;
        public IntPtr Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateAppContainerProfile(string appContainerName, string displayName, string description, IntPtr capabilities, uint capabilityCount, out IntPtr appContainerSid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeriveAppContainerSidFromAppContainerName(string appContainerName, out IntPtr appContainerSid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeleteAppContainerProfile(string appContainerName);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int GetAppContainerFolderPath(string appContainerSid, out IntPtr path);

    [DllImport("advapi32.dll")]
    private static extern IntPtr FreeSid(IntPtr sid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, uint flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, IntPtr startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, uint informationClass, ref JobObjectExtendedLimitInformation information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess, out IntPtr targetHandle, uint desiredAccess, bool inheritHandle, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
