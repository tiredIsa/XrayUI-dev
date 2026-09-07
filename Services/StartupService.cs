using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XrayUI.Helpers;
using System.Security.Principal;

namespace XrayUI.Services
{
    /// <summary>
    /// Manages autostart via a per-user Task Scheduler task, using direct COM
    /// vtable dispatch (no RCW / ComWrappers) so it works cleanly under
    /// NativeAOT without needing BuiltInComInteropSupport.
    /// </summary>
    public class StartupService
    {
        // Shared with App.xaml.cs so the flag string lives in exactly one place.
        // The boot task always passes this; auto-connect-on-boot is a separate
        // setting (AppSettings.IsAutoConnect) evaluated by MainViewModel.
        public const string StartupMinimizedArgument = StartupTaskDefinition.StartupMinimizedArgument;

        private const string TaskName = "XrayUI_Autostart";
        private const int TASK_CREATE_OR_UPDATE        = 6;
        private const int TASK_LOGON_INTERACTIVE_TOKEN = 3;
        private const uint CLSCTX_INPROC_SERVER        = 0x1;
        private const int E_FILENOTFOUND               = unchecked((int)0x80070002);

        private static readonly Guid CLSID_TaskScheduler = new("0F87369F-A4E5-4CFC-BD3E-73E6154572DD");
        private static readonly Guid IID_ITaskService    = new("2FABA4C7-4DA9-4013-9697-20CC3FD40F85");

        private static readonly string _exePath =
            Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

        public bool IsStartupEnabled()
        {
            try
            {
                IntPtr service = CreateAndConnectService();
                try
                {
                    IntPtr folder = TaskServiceGetFolder(service, @"\");
                    try
                    {
                        int hr = TaskFolderGetTask(folder, TaskName, out IntPtr pTask);
                        if (hr == E_FILENOTFOUND) return false;
                        Marshal.ThrowExceptionForHR(hr);
                        Release(pTask);
                        return true;
                    }
                    finally { Release(folder); }
                }
                finally { Release(service); }
            }
            catch
            {
                return false;
            }
        }

        public void SetStartupEnabled(bool enabled, bool runElevated = false)
        {
            // Direct COM calls do not initialize an apartment automatically under
            // NativeAOT. This method also runs on pool threads and in the helper.
            int initializationResult = CoInitializeEx(IntPtr.Zero, 0);
            const int changedApartmentMode = unchecked((int)0x80010106);
            if (initializationResult != changedApartmentMode)
                Marshal.ThrowExceptionForHR(initializationResult);
            try
            {
                if (enabled)
                {
                    if (string.IsNullOrEmpty(_exePath))
                        throw new InvalidOperationException("Cannot resolve exe path.");
                    RegisterTaskXml(runElevated);
                }
                else
                {
                    DeleteTaskIfExists();
                }
            }
            finally
            {
                if (initializationResult >= 0)
                    CoUninitialize();
            }
        }

        public const string ConfigureArgument = "--configure-startup=";
        private const string OwnerArgument = "--startup-owner=";

        private readonly SemaphoreSlim _configurationLock = new(1, 1);

        public async Task<bool> ConfigureAsync(bool enabled, bool runElevated)
        {
            await _configurationLock.WaitAsync();
            try
            {
                return await ConfigureCoreAsync(enabled, runElevated);
            }
            finally
            {
                _configurationLock.Release();
            }
        }

        private async Task<bool> ConfigureCoreAsync(bool enabled, bool runElevated)
        {
            if (!runElevated || AdminHelper.IsAdministrator())
            {
                try
                {
                    await Task.Run(() => SetStartupEnabled(enabled, runElevated));
                    return true;
                }
                catch (Exception ex) when (ex.HResult == unchecked((int)0x80070005) && !AdminHelper.IsAdministrator())
                {
                    // Editing/deleting a previously elevated task may also need UAC.
                }
            }

            var operation = !enabled ? "off" : runElevated ? "elevated" : "standard";
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrEmpty(sid)) throw new InvalidOperationException("Cannot resolve the current Windows account.");
            var startInfo = new ProcessStartInfo
            {
                FileName = _exePath,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.ArgumentList.Add(ConfigureArgument + operation);
            startInfo.ArgumentList.Add(OwnerArgument + sid);
            try
            {
                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Cannot start the startup configuration helper.");
                await process.WaitForExitAsync();
                if (process.ExitCode == 2)
                    throw new InvalidOperationException(Loc.GetString("Startup_SameAccountRequired"));
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(Loc.GetString("Startup_ConfigurationFailed"));
                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return false; // UAC cancelled: caller must leave its settings unchanged.
            }
        }

        public static bool IsConfigurationLaunch(string[] arguments) =>
            arguments.Any(argument => argument.StartsWith(ConfigureArgument, StringComparison.Ordinal));

        public static int RunConfiguration(string[] arguments)
        {
            try
            {
                var operation = arguments.Single(a => a.StartsWith(ConfigureArgument, StringComparison.Ordinal))[ConfigureArgument.Length..];
                var requestedOwner = arguments.Single(a => a.StartsWith(OwnerArgument, StringComparison.Ordinal))[OwnerArgument.Length..];
                // Credential elevation to a different account must not create a task
                // for that account or read/write its application settings.
                if (requestedOwner != WindowsIdentity.GetCurrent().User?.Value) return 2;
                if (!AdminHelper.IsAdministrator() || operation is not ("off" or "standard" or "elevated")) return 1;
                new StartupService().SetStartupEnabled(operation != "off", operation == "elevated");
                return 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Startup] Configuration failed: {ex}");
                return 1;
            }
        }

        private static void RegisterTaskXml(bool runElevated)
        {
            IntPtr service = CreateAndConnectService();
            try
            {
                IntPtr folder = TaskServiceGetFolder(service, @"\");
                try
                {
                    IntPtr registered = TaskFolderRegisterTask(
                        folder, TaskName, StartupTaskDefinition.Build(_exePath, WindowsIdentity.GetCurrent().User?.Value ?? "", runElevated),
                        TASK_CREATE_OR_UPDATE, TASK_LOGON_INTERACTIVE_TOKEN);
                    Release(registered);
                }
                finally { Release(folder); }
            }
            finally { Release(service); }
        }

        private static void DeleteTaskIfExists()
        {
            IntPtr service = CreateAndConnectService();
            try
            {
                IntPtr folder = TaskServiceGetFolder(service, @"\");
                try
                {
                    int hr = TaskFolderDeleteTask(folder, TaskName);
                    if (hr == E_FILENOTFOUND) return;
                    Marshal.ThrowExceptionForHR(hr);
                }
                finally { Release(folder); }
            }
            finally { Release(service); }
        }

        // ── COM vtable dispatch ───────────────────────────────────────────────
        //
        // Vtable layout for an IDispatch-derived interface:
        //   [0..2]  IUnknown     (QueryInterface, AddRef, Release)
        //   [3..6]  IDispatch    (GetTypeInfoCount, GetTypeInfo, GetIDsOfNames, Invoke)
        //   [7..]   interface-specific methods in IDL declaration order
        //
        // ITaskService:  GetFolder=7, GetRunningTasks=8, NewTask=9, Connect=10, ...
        // ITaskFolder:   get_Name=7, get_Path=8, GetFolder=9, GetFolders=10,
        //                CreateFolder=11, DeleteFolder=12, GetTask=13, GetTasks=14,
        //                DeleteTask=15, RegisterTask=16, ...

        private static unsafe IntPtr CreateAndConnectService()
        {
            Guid clsid = CLSID_TaskScheduler;
            Guid iid   = IID_ITaskService;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out IntPtr pService);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                Variant empty = default;
                void** vtbl = *(void***)pService;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, Variant, Variant, Variant, Variant, int>)vtbl[10];
                int connectHr = fn(pService, empty, empty, empty, empty);
                Marshal.ThrowExceptionForHR(connectHr);
                return pService;
            }
            catch
            {
                Release(pService);
                throw;
            }
        }

        private static unsafe IntPtr TaskServiceGetFolder(IntPtr pService, string path)
        {
            IntPtr bstr = Marshal.StringToBSTR(path);
            try
            {
                IntPtr pFolder;
                void** vtbl = *(void***)pService;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)vtbl[7];
                int hr = fn(pService, bstr, &pFolder);
                Marshal.ThrowExceptionForHR(hr);
                return pFolder;
            }
            finally { Marshal.FreeBSTR(bstr); }
        }

        private static unsafe int TaskFolderGetTask(IntPtr pFolder, string name, out IntPtr pTask)
        {
            IntPtr bstr = Marshal.StringToBSTR(name);
            try
            {
                IntPtr task;
                void** vtbl = *(void***)pFolder;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)vtbl[13];
                int hr = fn(pFolder, bstr, &task);
                pTask = task;
                return hr;
            }
            finally { Marshal.FreeBSTR(bstr); }
        }

        private static unsafe int TaskFolderDeleteTask(IntPtr pFolder, string name)
        {
            IntPtr bstr = Marshal.StringToBSTR(name);
            try
            {
                void** vtbl = *(void***)pFolder;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int>)vtbl[15];
                return fn(pFolder, bstr, 0);
            }
            finally { Marshal.FreeBSTR(bstr); }
        }

        private static unsafe IntPtr TaskFolderRegisterTask(
            IntPtr pFolder, string name, string xml, int createFlags, int logonType)
        {
            IntPtr bstrName = Marshal.StringToBSTR(name);
            IntPtr bstrXml  = Marshal.StringToBSTR(xml);
            try
            {
                Variant empty = default;
                IntPtr registered;
                void** vtbl = *(void***)pFolder;
                var fn = (delegate* unmanaged[Stdcall]<
                    IntPtr, IntPtr, IntPtr, int,
                    Variant, Variant, int, Variant,
                    IntPtr*, int>)vtbl[16];
                int hr = fn(pFolder, bstrName, bstrXml, createFlags,
                            empty, empty, logonType, empty, &registered);
                Marshal.ThrowExceptionForHR(hr);
                return registered;
            }
            finally
            {
                Marshal.FreeBSTR(bstrName);
                Marshal.FreeBSTR(bstrXml);
            }
        }

        private static unsafe void Release(IntPtr pUnk)
        {
            if (pUnk == IntPtr.Zero) return;
            void** vtbl = *(void***)pUnk;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2];
            fn(pUnk);
        }

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern void CoUninitialize();

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoCreateInstance(
            ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
            ref Guid riid, out IntPtr ppv);

        // Matches Win32 VARIANT: 16 bytes on x86, 24 on x64/ARM64.
        // Zero-initialized → VT_EMPTY, which is what ITaskService/Register expect
        // for "use default" in their optional VARIANT parameters.
        [StructLayout(LayoutKind.Sequential)]
        private struct Variant
        {
            public ushort vt;
            public ushort r1, r2, r3;
            public IntPtr data1;
            public IntPtr data2;
        }

    }
}
