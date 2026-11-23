using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Lineup.Tools.PerformanceCollector;

/// <summary>Contains Windows-specific access boundaries for a temporary, same-account collector.</summary>
[SupportedOSPlatform("windows")]
internal static class CollectorPlatform
{
    /// <summary>Gets the fixed private spool location beneath a normally administrator-writable parent.</summary>
    internal static string GetSpoolRoot(string sessionId)
    {
        ValidateSessionId(sessionId);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), $"Lineup.Performance.{sessionId}");
    }

    /// <summary>Rejects caller-controlled paths masquerading as session identifiers.</summary>
    internal static void ValidateSessionId(string sessionId)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _))
        {
            throw new InvalidDataException("The collector session must be a GUID in N format.");
        }
    }

    /// <summary>Creates a private spool that the unelevated account can read but cannot modify or redirect.</summary>
    internal static string CreateSpool(string sessionId, SecurityIdentifier user)
    {
        var path = GetSpoolRoot(sessionId);
        if (Directory.Exists(path) || File.Exists(path))
        {
            throw new IOException("Collector spool already exists; choose a fresh session.");
        }
        new DirectoryInfo(path).Create(CreateSpoolSecurity(user));
        return path;
    }

    /// <summary>Keeps children read-only to the ordinary token even on machines whose policy assigns creator ownership.</summary>
    internal static DirectorySecurity CreateSpoolSecurity(SecurityIdentifier user)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.SetOwner(administrators);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { administrators, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        }
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        // OWNER_RIGHTS suppresses an owner's implicit WRITE_DAC; the elevated Administrators grant remains effective.
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-3-4"), FileSystemRights.ReadPermissions, inheritance, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>Creates a local pipe with an explicit cross-elevation ACL, not CurrentUserOnly's same-elevation restriction.</summary>
    internal static NamedPipeServerStream CreatePipe(string sessionId, SecurityIdentifier user)
    {
        ValidateSessionId(sessionId);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var pipe = NamedPipeServerStreamAcl.Create($"Lineup.Performance.{sessionId}", PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security, HandleInheritability.None, PipeAccessRights.TakeOwnership);
        try
        {
            // Only the data pipe is medium-integrity. The elevated process and read-only private spool retain their protection.
            var label = new RawSecurityDescriptor("S:(ML;;NW;;;ME)").SystemAcl ?? throw new InvalidOperationException("Pipe integrity label is unavailable.");
            var bytes = new byte[label.BinaryLength];
            label.GetBinaryForm(bytes, 0);
            var error = SetSecurityInfo(pipe.SafePipeHandle, 6, 0x10, 0, 0, 0, bytes);
            if (error != 0)
            {
                throw new Win32Exception(checked((int)error), "Could not set the cross-elevation pipe integrity boundary.");
            }
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    /// <summary>Checks the client's account and desktop session without running any client operation while impersonating it.</summary>
    internal static void ValidateClient(NamedPipeServerStream pipe, SecurityIdentifier expectedUser)
    {
        SecurityIdentifier? actualUser = null;
        var networkLogon = false;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            actualUser = identity.User;
            networkLogon = identity.Groups?.Contains(new SecurityIdentifier(WellKnownSidType.NetworkSid, null)) == true;
        });
        if (networkLogon || actualUser != expectedUser)
        {
            throw new UnauthorizedAccessException("The collector accepts only local clients from the approving account.");
        }
        if (!GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var sessionId))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        using var self = Process.GetCurrentProcess();
        if (sessionId != self.SessionId)
        {
            throw new UnauthorizedAccessException("The collector client must be in the same Windows session.");
        }
    }

    /// <summary>Rejects pipe squatting by binding the connected server to its protected published process lifetime.</summary>
    internal static void ValidateServer(NamedPipeClientStream pipe, CollectorSession expected)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        using var server = Process.GetProcessById(checked((int)processId));
        using var self = Process.GetCurrentProcess();
        if (server.Id != expected.ProcessId || server.StartTime.ToUniversalTime().Ticks != expected.StartedUtcTicks || server.SessionId != self.SessionId)
        {
            throw new UnauthorizedAccessException("Connected pipe does not belong to the approved collector process.");
        }
    }

    /// <summary>Checks exact target lifetimes without terminating or reconfiguring targets.</summary>
    internal static void ValidateRoots(IEnumerable<CollectorRoot> roots)
    {
        foreach (var root in roots)
        {
            using var process = Process.GetProcessById(root.ProcessId);
            if (process.StartTime.ToUniversalTime().Ticks != root.StartedUtcTicks || process.HasExited)
            {
                throw new InvalidDataException("A selected root exited or its PID was reused; select the target again.");
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern uint SetSecurityInfo(SafePipeHandle handle, int objectType, uint securityInformation, nint owner, nint group, nint dacl, byte[] sacl);
}
