using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace LanAdmin.SetupWorker;

internal static class ServiceManager
{
    private const uint ScManagerAllAccess = 0xF003F;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceErrorNormal = 0x00000001;
    private const int ServiceConfigFailureActions = 2;
    private const int ScActionRestart = 1;

    public static void CreateOrReplaceService(string serviceName, string displayName, string executablePath)
    {
        using var manager = OpenServiceManager();
        var binaryPath = $"\"{executablePath}\"";
        var serviceHandle = NativeMethods.CreateService(
            manager,
            serviceName,
            displayName,
            ServiceAllAccess,
            ServiceWin32OwnProcess,
            ServiceAutoStart,
            ServiceErrorNormal,
            binaryPath,
            null,
            IntPtr.Zero,
            null,
            null,
            null);

        if (serviceHandle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ErrorServiceExists)
            {
                throw new Win32Exception(error, $"Failed to create service '{serviceName}'.");
            }

            using var existingService = OpenService(manager, serviceName, throwIfMissing: true)
                ?? throw new Win32Exception(error, $"Failed to open existing service '{serviceName}'.");
            UpdateService(existingService, serviceName, displayName, binaryPath);
            StartService(serviceName);
            return;
        }

        using var service = new SafeServiceHandle(serviceHandle);
        ConfigureFailureActions(serviceName, service);
        StartService(serviceName);
    }

    public static void StopServiceIfExists(string serviceName)
    {
        StopServiceIfRunning(serviceName);
    }

    public static void RemoveServiceIfExists(string serviceName)
    {
        StopServiceIfRunning(serviceName);

        using var manager = OpenServiceManager();
        using var service = OpenService(manager, serviceName, throwIfMissing: false);
        if (service is null)
        {
            return;
        }

        if (!NativeMethods.DeleteService(service.DangerousGetHandle()))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ErrorServiceMarkedForDelete)
            {
                throw new Win32Exception(error, $"Failed to delete service '{serviceName}'.");
            }
        }

        WaitForServiceDeletion(serviceName);
    }

    private static SafeServiceHandle OpenServiceManager()
    {
        var handle = NativeMethods.OpenSCManager(null, null, ScManagerAllAccess);
        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open the Service Control Manager.");
        }

        return new SafeServiceHandle(handle);
    }

    private static SafeServiceHandle? OpenService(SafeServiceHandle manager, string serviceName, bool throwIfMissing)
    {
        var handle = NativeMethods.OpenService(manager.DangerousGetHandle(), serviceName, ServiceAllAccess);
        if (handle != IntPtr.Zero)
        {
            return new SafeServiceHandle(handle);
        }

        var error = Marshal.GetLastWin32Error();
        if (!throwIfMissing && error == NativeMethods.ErrorServiceDoesNotExist)
        {
            return null;
        }

        throw new Win32Exception(error, $"Failed to open service '{serviceName}'.");
    }

    private static void StopServiceIfRunning(string serviceName)
    {
        using var controller = TryGetServiceController(serviceName);
        if (controller is null)
        {
            return;
        }

        if (controller.Status == ServiceControllerStatus.Stopped)
        {
            return;
        }

        if (controller.Status != ServiceControllerStatus.StopPending)
        {
            controller.Stop();
        }

        controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    }

    private static void StartService(string serviceName)
    {
        using var controller = new ServiceController(serviceName);
        if (controller.Status != ServiceControllerStatus.Running)
        {
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }
    }

    private static void ConfigureFailureActions(string serviceName, SafeServiceHandle service)
    {
        var actions = new[]
        {
            new NativeMethods.ScAction(ScActionRestart, 5000),
            new NativeMethods.ScAction(ScActionRestart, 5000),
            new NativeMethods.ScAction(ScActionRestart, 5000)
        };

        var actionSize = Marshal.SizeOf<NativeMethods.ScAction>();
        var actionsBuffer = Marshal.AllocHGlobal(actionSize * actions.Length);

        try
        {
            for (var index = 0; index < actions.Length; index++)
            {
                Marshal.StructureToPtr(actions[index], actionsBuffer + (index * actionSize), fDeleteOld: false);
            }

            var failureActions = new NativeMethods.ServiceFailureActions
            {
                ResetPeriod = 86400,
                RebootMessage = null,
                Command = null,
                ActionsCount = actions.Length,
                Actions = actionsBuffer
            };

            if (!NativeMethods.ChangeServiceConfig2(service.DangerousGetHandle(), ServiceConfigFailureActions, ref failureActions))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to configure failure actions for service '{serviceName}'.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(actionsBuffer);
        }
    }

    private static void UpdateService(SafeServiceHandle service, string serviceName, string displayName, string binaryPath)
    {
        StopServiceIfRunning(serviceName);

        if (!NativeMethods.ChangeServiceConfig(
                service.DangerousGetHandle(),
                ServiceWin32OwnProcess,
                ServiceAutoStart,
                ServiceErrorNormal,
                binaryPath,
                null,
                IntPtr.Zero,
                null,
                null,
                null,
                displayName))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to update service '{serviceName}'.");
        }

        ConfigureFailureActions(serviceName, service);
    }

    private static void WaitForServiceDeletion(string serviceName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (!ServiceExists(serviceName))
            {
                return;
            }

            Thread.Sleep(500);
        }

        throw new System.TimeoutException($"Timed out waiting for service '{serviceName}' to be deleted.");
    }

    private static ServiceController? TryGetServiceController(string serviceName)
    {
        try
        {
            var controller = new ServiceController(serviceName);
            _ = controller.Status;
            return controller;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool ServiceExists(string serviceName)
    {
        return ServiceController.GetServices()
            .Any(service => string.Equals(service.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));
    }
}
